namespace LootTracker
{
    using System;
    using GameHelper.Plugin;

    public sealed class LootTrackerSettings : IPSettings
    {
        // How many completed-map rows to keep in the session history (table + memory); oldest dropped past this.
        public int HistorySize = 50;

        // How many finished sessions to keep on disk (config/sessions); oldest deleted past this.
        public int MaxSessions = 30;

        // poe.ninja PoE2 league slug (spaces become '+'). Update each league launch. Shared default
        // with RunecraftHelper as of 2026-06.
        public string League = "Runes of Aldur";

        // How long cached prices stay valid before a re-fetch (minutes; UI slider 5–60).
        public int CacheTtlMinutes = 60;

        // Last successful price sync (UTC). Zero = never fetched.
        public DateTime LastSyncUtc = DateTime.MinValue;

        // Pixels from the bottom of the game window to the map strip's bottom edge. Used only as a
        // fallback when the experience-bar element can't be located (otherwise the strip auto-anchors
        // just above it).
        public float BarBottomOffset = 5f;

        // Horizontal side for the map strip: true = right (default), false = left.
        public bool BarOnRight = true;

        // Background opacity of the map strip and the compact hideout bar (0 = transparent, 1 = opaque).
        public float BarOpacity = 0.55f;

        // Show the per-rarity monster-kill counts (Normal/Magic/Rare/Unique) on the map strip.
        public bool ShowKills = true;

        // Height (px) of the compact hideout bar.
        public float CompactHeight = 115f;

        // Manual multiplier on top of the auto game-UI scale (DisplaySize.Y / 1600) applied to the
        // bars' font and fixed metrics, so the overlay shrinks/grows with the game HUD across
        // resolutions. 1.0 = pure auto scale; tune per setup if the auto factor isn't ideal.
        public float UiScale = 1.2f;
    }
}
