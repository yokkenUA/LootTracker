namespace LootTracker
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using ImGuiNET;
    using Newtonsoft.Json;

    // ── Persisted session-history model ──────────────────────────────────
    // One priced line of a map's net gains, snapshotted at session-save time (so a historical record
    // stays stable regardless of later price moves).
    public sealed class SessionLootLine
    {
        public string Label = string.Empty; // English display name (or art id fallback)
        public long Count;
        public double Ex;                    // total Exalted value of this line at save time (0 if unpriced)
        public bool Priced;
    }

    // A completed map within a saved session.
    public sealed class SessionMap
    {
        public string Name = string.Empty;
        public double ActiveSeconds;         // wall time spent inside the map
        public double ProfitEx;              // Σ line.Ex
        public List<SessionLootLine> Loot = new();
    }

    // A finished tracking session (everything between two "New session" presses).
    public sealed class SessionRecord
    {
        public DateTime StartUtc;
        public DateTime EndUtc;
        public double DivineRate;            // Divine→Exalted rate at save time (0 = unknown)
        public List<SessionMap> Maps = new();

        // On-disk path, filled on load so the row can be deleted. Not serialized.
        [JsonIgnore]
        public string FilePath = string.Empty;

        public double TotalEx()
        {
            double s = 0;
            foreach (var m in this.Maps) s += m.ProfitEx;
            return s;
        }

        public double TotalActiveSeconds()
        {
            double s = 0;
            foreach (var m in this.Maps) s += m.ActiveSeconds;
            return s;
        }

        public double PerHourEx()
        {
            double h = this.TotalActiveSeconds() / 3600.0;
            return h > 0 ? this.TotalEx() / h : 0;
        }

        public double ToDivine(double ex) => this.DivineRate > 0 ? ex / this.DivineRate : 0;
    }

    public sealed partial class LootTrackerCore
    {
        private string SessionsDir => Path.Join(this.DllDirectory, "config", "sessions");

        // History-window state (transient, not persisted).
        private bool showSessionHistory;
        private List<SessionRecord>? sessionCache;
        private SessionRecord? detailSession;
        private SessionMap? lootMap;

        // ── Persistence ──────────────────────────────────────────────────
        // Snapshot the session being ended (the completed runs, plus the in-progress one banked first)
        // and write it to its own json under config/sessions. No-op when nothing was run.
        private void SaveCurrentSession()
        {
            this.BankActiveTime(DateTime.UtcNow); // include the active run's still-running time
            if (this.completed.Count == 0)
            {
                return;
            }

            var rec = this.BuildSessionRecord(this.sessionStartUtc, DateTime.UtcNow);
            this.WriteSession(rec);
            this.TrimSessions();
        }

        // Price every completed run's net gains at the current rates and assemble the record.
        private SessionRecord BuildSessionRecord(DateTime startUtc, DateTime endUtc)
        {
            var rec = new SessionRecord
            {
                StartUtc = startUtc,
                EndUtc = endUtc,
                DivineRate = this.priceCache.DivineToExaltedRate,
            };

            foreach (var r in this.completed)
            {
                var m = new SessionMap { Name = r.Name, ActiveSeconds = r.ActiveTime.TotalSeconds };
                double profit = 0;
                foreach (var kv in r.Gained)
                {
                    if (kv.Value == 0) continue;
                    var artKey = this.PriceKey(kv.Key);
                    bool priced = this.priceCache.TryGetPriceByArtId(artKey, out var unit) && unit > 0;
                    double ex = priced ? unit * kv.Value : 0;
                    var label = this.priceCache.TryGetNameByArtId(artKey, out var nm) && nm.Length > 0 ? nm : artKey;
                    m.Loot.Add(new SessionLootLine { Label = label, Count = kv.Value, Ex = ex, Priced = priced });
                    profit += ex;
                }

                m.Loot.Sort((a, b) => Math.Abs(b.Ex).CompareTo(Math.Abs(a.Ex)));
                m.ProfitEx = profit;
                rec.Maps.Add(m);
            }

            return rec;
        }

        private void WriteSession(SessionRecord rec)
        {
            try
            {
                Directory.CreateDirectory(this.SessionsDir);
                // Timestamp filename → lexical order == chronological order (used by the trim).
                var name = $"session_{rec.StartUtc:yyyyMMdd_HHmmss_fff}.json";
                File.WriteAllText(Path.Join(this.SessionsDir, name), JsonConvert.SerializeObject(rec, Formatting.Indented));
            }
            catch
            {
                // history is best-effort; a failed write just means this session isn't archived.
            }
        }

        // Drop the oldest session files past the keep-limit.
        private void TrimSessions()
        {
            try
            {
                if (!Directory.Exists(this.SessionsDir)) return;
                var files = Directory.GetFiles(this.SessionsDir, "session_*.json");
                int max = Math.Max(1, this.Settings.MaxSessions);
                if (files.Length <= max) return;
                Array.Sort(files, StringComparer.Ordinal); // oldest first
                for (int i = 0; i < files.Length - max; i++)
                {
                    File.Delete(files[i]);
                }
            }
            catch
            {
                // ignore
            }
        }

        // (Re)load all saved sessions from disk into the cache, newest first.
        private void LoadSessions()
        {
            var list = new List<SessionRecord>();
            try
            {
                if (Directory.Exists(this.SessionsDir))
                {
                    foreach (var path in Directory.GetFiles(this.SessionsDir, "session_*.json"))
                    {
                        try
                        {
                            var rec = JsonConvert.DeserializeObject<SessionRecord>(File.ReadAllText(path));
                            if (rec != null)
                            {
                                rec.FilePath = path;
                                list.Add(rec);
                            }
                        }
                        catch
                        {
                            // skip a corrupt file
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            list.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
            this.sessionCache = list;
        }

        private void DeleteSession(SessionRecord rec)
        {
            try
            {
                if (!string.IsNullOrEmpty(rec.FilePath) && File.Exists(rec.FilePath))
                {
                    File.Delete(rec.FilePath);
                }
            }
            catch
            {
                // ignore
            }

            this.sessionCache?.Remove(rec);
            if (ReferenceEquals(this.detailSession, rec))
            {
                this.detailSession = null;
                this.lootMap = null;
            }
        }

        // ── History windows ──────────────────────────────────────────────
        // Top-level list of saved sessions: date · length · maps · total Divine · Divine/h, with
        // per-row Details / Delete. Opened from the plugin settings button.
        private void DrawSessionHistoryWindow()
        {
            if (!this.showSessionHistory)
            {
                return;
            }

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(760, 380), ImGuiCond.FirstUseEver);
            bool open = true;
            if (ImGui.Begin("Session history", ref open))
            {
                if (ImGui.Button("Refresh"))
                {
                    this.LoadSessions();
                }

                ImGui.SameLine();
                ImGui.TextDisabled($"{this.sessionCache?.Count ?? 0} saved · keeping last {this.Settings.MaxSessions}");

                if (ImGui.BeginTable("sessions_tbl", 6,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Date");
                    ImGui.TableSetupColumn("Length", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("Maps", ImGuiTableColumnFlags.WidthFixed, 50);
                    ImGui.TableSetupColumn("Total div", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableSetupColumn("Div/h", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableHeadersRow();

                    SessionRecord? toDelete = null;
                    var cache = this.sessionCache;
                    if (cache != null)
                    {
                        for (int i = 0; i < cache.Count; i++)
                        {
                            var rec = cache[i];
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted($"{rec.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(FormatDuration(rec.EndUtc - rec.StartUtc));
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(rec.Maps.Count.ToString());
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(rec.DivineRate > 0 ? $"{rec.ToDivine(rec.TotalEx()):0.0}" : "—");
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(rec.DivineRate > 0 ? $"{rec.ToDivine(rec.PerHourEx()):0.0}" : "—");
                            ImGui.TableNextColumn();
                            if (ImGui.SmallButton($"Details##s{i}"))
                            {
                                this.detailSession = rec;
                                this.lootMap = null;
                            }

                            ImGui.SameLine();
                            if (ImGui.SmallButton($"Delete##s{i}"))
                            {
                                toDelete = rec;
                            }
                        }
                    }

                    ImGui.EndTable();

                    if (toDelete != null)
                    {
                        this.DeleteSession(toDelete);
                    }
                }
            }

            ImGui.End();
            if (!open)
            {
                this.showSessionHistory = false;
            }
        }

        // One session in full: the six aggregates + the per-map table, each map openable for its loot.
        private void DrawSessionDetailWindow()
        {
            var rec = this.detailSession;
            if (rec == null)
            {
                return;
            }

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(620, 420), ImGuiCond.FirstUseEver);
            bool open = true;
            if (ImGui.Begin($"Session {rec.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}###session_detail", ref open))
            {
                int maps = rec.Maps.Count;
                double totalEx = rec.TotalEx();
                double activeS = rec.TotalActiveSeconds();
                var avgTime = maps > 0 ? TimeSpan.FromSeconds(activeS / maps) : TimeSpan.Zero;
                double avgEx = maps > 0 ? totalEx / maps : 0;
                string totalDiv = rec.DivineRate > 0 ? $"{rec.ToDivine(totalEx):0.0}" : "—";
                string hourDiv = rec.DivineRate > 0 ? $"{rec.ToDivine(rec.PerHourEx()):0.0}" : "—";

                ImGui.Text($"Duration: {FormatDuration(rec.EndUtc - rec.StartUtc)}");
                ImGui.Text($"Maps completed: {maps}");
                ImGui.Text($"AVG time in map: {FormatDuration(avgTime)}");
                ImGui.Text($"AVG profit: {avgEx:0} Ex");
                ImGui.Text($"Total: {totalDiv} Divine");
                ImGui.Text($"Per hour: {hourDiv} Divine / hour");
                ImGui.Separator();

                if (ImGui.BeginTable("detail_maps_tbl", 4,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Map");
                    ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 90);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableHeadersRow();

                    for (int i = 0; i < rec.Maps.Count; i++)
                    {
                        var m = rec.Maps[i];
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(m.Name);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(FormatDuration(TimeSpan.FromSeconds(m.ActiveSeconds)));
                        ImGui.TableNextColumn();
                        ImGui.TextColored(m.ProfitEx >= 0 ? GreenCol : RedCol, $"{m.ProfitEx:+0.0;-0.0;0} ex");
                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton($"Loot##m{i}"))
                        {
                            this.lootMap = m;
                        }
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.End();
            if (!open)
            {
                this.detailSession = null;
                this.lootMap = null;
            }
        }

        // What a single map dropped (priced lines, snapshotted at save time).
        private void DrawMapLootWindow()
        {
            var m = this.lootMap;
            if (m == null)
            {
                return;
            }

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(420, 360), ImGuiCond.FirstUseEver);
            bool open = true;
            if (ImGui.Begin($"Loot — {m.Name}###map_loot", ref open))
            {
                ImGui.TextColored(m.ProfitEx >= 0 ? GreenCol : RedCol, $"{m.ProfitEx:+0.0;-0.0;0} ex");
                ImGui.SameLine();
                ImGui.TextDisabled($"· {FormatDuration(TimeSpan.FromSeconds(m.ActiveSeconds))}");
                ImGui.Separator();

                if (ImGui.BeginTable("map_loot_tbl", 3,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Item");
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 90);
                    ImGui.TableHeadersRow();

                    foreach (var line in m.Loot)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(line.Label);
                        ImGui.TableNextColumn();
                        ImGui.TextColored(line.Count >= 0 ? GreenCol : RedCol, $"{line.Count:+0;-0}");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(line.Priced ? $"{line.Ex:+0.0;-0.0;0} ex" : "—");
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.End();
            if (!open)
            {
                this.lootMap = null;
            }
        }
    }
}
