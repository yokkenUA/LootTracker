namespace LootTracker
{
    using System;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using GameHelper;
    using ImGuiNET;

    // ── Experience-bar resolution by Flags fingerprint (fork-independent) ─────────────────────────
    // The HUD bars anchor to the in-game experience bar. Rather than depend on a fork-specific
    // GameUi.ExperienceBar member (absent on most forks), we resolve it ourselves the way the Atlas /
    // RunecraftHelper plugins do: walk GameUi's UI tree matching each hop's Flags fingerprint with
    // backtracking. Child indices drift across game restarts/patches; the role-encoding Flags bits are
    // stable, so we match those (with the IsVisible bit masked out, since it toggles). The path is:
    //   GameUi -> {fp 0x005026F1} -> {fp 0x004426F3 = experience bar}
    // Flags verified live on PoE2 0.5.x. Geometry math mirrors GameHelper.UiElementBase.Position/Size
    // but is read by raw offset (not the GameOffsets struct) so it survives layout differences between
    // forks' GameOffsets builds.
    public sealed partial class LootTrackerCore
    {
        private static readonly uint[] ExpBarFingerprints = { 0x005026F1, 0x004426F3 };

        private const int UiChildrenOffset = 0x10;          // StdVector {first,last} of child element ptrs
        private const int UiSelfOffset = 0x08;              // points back at the element (validity check)
        private const int UiFlagsOffset = 0x180;
        private const int UiRelativePositionOffset = 0x118; // StdTuple2D<float>
        private const int UiPositionModifierOffset = 0xF0;  // StdTuple2D<float>
        private const int UiParentPtrOffset = 0xB8;
        private const int UiLocalScaleOffset = 0x130;       // float
        private const int UiScaleIndexOffset = 0x18A;       // byte
        private const int UiUnscaledSizeOffset = 0x288;     // StdTuple2D<float>
        private const uint UiIsVisibleMask = 1u << 0x0B;    // 0x800
        private const uint UiShouldModifyPosMask = 1u << 0x0A; // 0x400
        private const int UiNodeReadSize = 0x290;           // covers up to UnscaledSize + 8

        private IntPtr resolvedExpBar;
        private DateTime nextExpBarResolveUtc = DateTime.MinValue;

        private struct UiNode
        {
            public uint Flags;
            public Vector2 RelativePosition;
            public Vector2 PositionModifier;
            public IntPtr ParentPtr;
            public float LocalScaleMultiplier;
            public byte ScaleIndex;
            public Vector2 UnscaledSize;
        }

        // Resolve the experience bar by fingerprint walk and return its scaled screen rect.
        private bool TryGetExperienceBarRectByFp(out Vector2 pos, out Vector2 size)
        {
            pos = default;
            size = default;
            if (!this.EnsureProcess())
            {
                return false;
            }

            var addr = this.GetExperienceBarAddress();
            if (addr == IntPtr.Zero || !this.TryReadUiNode(addr, out var el))
            {
                return false;
            }

            if ((el.Flags & UiIsVisibleMask) == 0)
            {
                return false;
            }

            size = this.ScaledSize(in el);
            if (!(size.X > 1f && size.Y > 1f) || !this.TryScreenPosition(in el, out pos))
            {
                return false;
            }

            return !float.IsNaN(pos.X) && !float.IsNaN(pos.Y);
        }

        // Cache the resolved address; revalidate it cheaply each call (Self self-pointer + terminal fp)
        // and only re-walk the tree on a throttle when it goes stale (e.g. after a zone transition
        // tears the UI down and rebuilds it).
        private IntPtr GetExperienceBarAddress()
        {
            if (this.resolvedExpBar != IntPtr.Zero)
            {
                if (this.ReadPtr(this.resolvedExpBar + UiSelfOffset) == this.resolvedExpBar &&
                    this.TryReadFlagsRaw(this.resolvedExpBar, out var f) &&
                    (f & ~UiIsVisibleMask) == (ExpBarFingerprints[^1] & ~UiIsVisibleMask))
                {
                    return this.resolvedExpBar;
                }

                this.resolvedExpBar = IntPtr.Zero;
            }

            var now = DateTime.UtcNow;
            if (now < this.nextExpBarResolveUtc)
            {
                return IntPtr.Zero;
            }

            this.nextExpBarResolveUtc = now.AddMilliseconds(500);
            var gameUi = Core.States.InGameStateObject?.GameUi?.Address ?? IntPtr.Zero;
            if (gameUi == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            this.resolvedExpBar = this.WalkFp(gameUi, ExpBarFingerprints, 0);
            return this.resolvedExpBar;
        }

        // Recursive backtracking fingerprint walk: at `step`, scan `parentAddr`'s children for ones
        // whose Flags (IsVisible bit masked) match fps[step], trying visible candidates before invisible
        // ones, and recurse until a branch reaches a valid experience bar at the bottom.
        private IntPtr WalkFp(IntPtr parentAddr, uint[] fps, int step)
        {
            if (step == fps.Length)
            {
                return this.IsExperienceBar(parentAddr) ? parentAddr : IntPtr.Zero;
            }

            if (!this.TryReadStdVector(parentAddr + UiChildrenOffset, out var first, out var last))
            {
                return IntPtr.Zero;
            }

            long n = ((long)last - (long)first) / 8;
            if (n <= 0 || n > 4000)
            {
                return IntPtr.Zero;
            }

            uint target = fps[step] & ~UiIsVisibleMask;
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantVisible = pass == 0;
                for (int i = 0; i < n; i++)
                {
                    var childAddr = this.ReadPtr(first + (nint)(i * 8));
                    if (childAddr == IntPtr.Zero || !this.TryReadFlagsRaw(childAddr, out var flags))
                    {
                        continue;
                    }

                    if ((flags & ~UiIsVisibleMask) != target)
                    {
                        continue;
                    }

                    if (((flags & UiIsVisibleMask) != 0) != wantVisible)
                    {
                        continue;
                    }

                    var deeper = this.WalkFp(childAddr, fps, step + 1);
                    if (deeper != IntPtr.Zero)
                    {
                        return deeper;
                    }
                }
            }

            return IntPtr.Zero;
        }

        // Terminal validation: the experience bar is a visible, genuinely wide bar — enough to reject
        // unrelated siblings that share its fingerprint but are degenerate / empty.
        private bool IsExperienceBar(IntPtr addr)
        {
            if (!this.TryReadUiNode(addr, out var el) || (el.Flags & UiIsVisibleMask) == 0)
            {
                return false;
            }

            var size = this.ScaledSize(in el);
            return size.X > 50f && size.Y > 2f;
        }

        // ── Geometry (mirrors GameHelper.UiElementBase.Position / Size) ───────────────────────────
        // The letterbox cull term (added to X on letterboxed displays) is omitted, matching the other
        // plugins' resolvers — it's 0 on standard full-window displays.
        private static (float W, float H) UiScaleValue(byte index, float multiplier)
        {
            var io = ImGui.GetIO();
            float v1 = io.DisplaySize.X / 2560f;
            float v2 = io.DisplaySize.Y / 1600f;
            float w = multiplier, h = multiplier;
            switch (index)
            {
                case 1: w *= v1; h *= v1; break;
                case 2: w *= v2; h *= v2; break;
                case 3: w *= v1; h *= v2; break;
            }

            return (w, h);
        }

        private Vector2 ScaledSize(in UiNode el)
        {
            var (w, h) = UiScaleValue(el.ScaleIndex, el.LocalScaleMultiplier);
            return new Vector2(el.UnscaledSize.X * w, el.UnscaledSize.Y * h);
        }

        private bool TryScreenPosition(in UiNode el, out Vector2 screen)
        {
            if (!this.TryGetUnscaledPosition(in el, 0, out var p))
            {
                screen = default;
                return false;
            }

            var (w, h) = UiScaleValue(el.ScaleIndex, el.LocalScaleMultiplier);
            screen = new Vector2(p.X * w, p.Y * h);
            return true;
        }

        // Recursive parent-chain walk — the exact arithmetic of UiElementBase.GetUnScaledPosition.
        // Returns false if an ancestor read fails, so the caller doesn't anchor to a half-resolved spot.
        private bool TryGetUnscaledPosition(in UiNode el, int depth, out Vector2 pos)
        {
            var local = el.RelativePosition;
            if (el.ParentPtr == IntPtr.Zero || depth >= 64)
            {
                pos = local;
                return true;
            }

            if (!this.TryReadUiNode(el.ParentPtr, out var parent))
            {
                pos = local;
                return false;
            }

            if (!this.TryGetUnscaledPosition(in parent, depth + 1, out var parentPos))
            {
                pos = local;
                return false;
            }

            if ((el.Flags & UiShouldModifyPosMask) != 0)
            {
                parentPos += parent.PositionModifier;
            }

            if (parent.ScaleIndex == el.ScaleIndex &&
                Math.Abs(parent.LocalScaleMultiplier - el.LocalScaleMultiplier) < 0.0001f)
            {
                pos = parentPos + local;
                return true;
            }

            var (psw, psh) = UiScaleValue(parent.ScaleIndex, parent.LocalScaleMultiplier);
            var (msw, msh) = UiScaleValue(el.ScaleIndex, el.LocalScaleMultiplier);
            pos = new Vector2(
                parentPos.X * psw / msw + local.X,
                parentPos.Y * psh / msh + local.Y);
            return true;
        }

        // ── Raw field reads ───────────────────────────────────────────────────────────────────────
        private bool TryReadFlagsRaw(IntPtr addr, out uint flags)
        {
            flags = 0;
            if (addr == IntPtr.Zero)
            {
                return false;
            }

            var buf = new byte[4];
            if (!ReadProcessMemory(this.processHandle, addr + UiFlagsOffset, buf, (uint)buf.Length, out _))
            {
                return false;
            }

            flags = BitConverter.ToUInt32(buf, 0);
            return true;
        }

        private bool TryReadUiNode(IntPtr addr, out UiNode node)
        {
            node = default;
            ulong u = (ulong)addr;
            if (u < 0x10000 || u > 0x7FFFFFFFFFFF)
            {
                return false;
            }

            var buf = new byte[UiNodeReadSize];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)UiNodeReadSize, out var got) ||
                got < UiNodeReadSize)
            {
                return false;
            }

            node.Flags = BitConverter.ToUInt32(buf, UiFlagsOffset);
            node.RelativePosition = new Vector2(
                BitConverter.ToSingle(buf, UiRelativePositionOffset),
                BitConverter.ToSingle(buf, UiRelativePositionOffset + 4));
            node.PositionModifier = new Vector2(
                BitConverter.ToSingle(buf, UiPositionModifierOffset),
                BitConverter.ToSingle(buf, UiPositionModifierOffset + 4));
            node.ParentPtr = (IntPtr)BitConverter.ToInt64(buf, UiParentPtrOffset);
            node.LocalScaleMultiplier = BitConverter.ToSingle(buf, UiLocalScaleOffset);
            node.ScaleIndex = buf[UiScaleIndexOffset];
            node.UnscaledSize = new Vector2(
                BitConverter.ToSingle(buf, UiUnscaledSizeOffset),
                BitConverter.ToSingle(buf, UiUnscaledSizeOffset + 4));
            return true;
        }
    }
}
