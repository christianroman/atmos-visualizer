// Dolby Atmos Master File set (DAMF), as written by truehdd:
//   NAME.atmos           header: bed channels and the object list, in audio channel order
//   NAME.atmos.metadata  event list per element (delta-encoded: a field appears only when it changes)
//   NAME.atmos.audio     CAF, one channel per element (see CafReader)
//
// DamfFile loads the two YAML files and resolves the delta-encoding, so every event carries
// the element's complete state at that moment.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AtmosEngine
{
    /// <summary>One element = one audio channel: a bed channel or an object.</summary>
    public sealed class DamfElement
    {
        public int Index;            // 0-based audio channel in .atmos.audio
        public int Id;               // DAMF ID
        public bool IsBed;
        public string BedChannel;    // e.g. "LFE", "L", "C"; null for objects
        public List<DamfEvent> Events = new List<DamfEvent>();

        public override string ToString() =>
            IsBed ? "ch" + (Index + 1) + " bed " + BedChannel + " (ID " + Id + ")" : "ch" + (Index + 1) + " object ID " + Id;
    }

    /// <summary>The complete state of an element from SamplePos on (fields carried forward).</summary>
    public struct DamfEvent
    {
        public long SamplePos;
        public bool Active;
        public double X, Y, Z;       // x -1 left..+1 right, y -1 rear..+1 front, z 0 floor..1 ceiling
        public double Size;
        public double GainDb;        // -inf for silent
        public long RampLength;      // samples over which the change from the previous event is applied
        public bool Snap;
        public string Zones;
        public bool Elevation;

        public double LinearGain => Active && !double.IsNegativeInfinity(GainDb) ? Math.Pow(10.0, GainDb / 20.0) : 0.0;
    }

    public sealed class DamfFile
    {
        public string AtmosPath { get; private set; }
        public string MetadataPath { get; private set; }
        public string AudioPath { get; private set; }
        public double SampleRate { get; private set; }
        public double Fps { get; private set; }
        public double Offset { get; private set; }
        public List<DamfElement> Elements { get; } = new List<DamfElement>();

        public IEnumerable<DamfElement> Objects => Elements.Where(e => !e.IsBed);
        public IEnumerable<DamfElement> Beds => Elements.Where(e => e.IsBed);

        public static DamfFile Load(string atmosPath)
        {
            var f = new DamfFile { AtmosPath = Path.GetFullPath(atmosPath) };
            string dir = Path.GetDirectoryName(f.AtmosPath);

            var root = (Dictionary<string, object>)MiniYaml.Parse(File.ReadAllText(atmosPath));
            var pres = (Dictionary<string, object>)((List<object>)root["presentations"])[0];
            f.MetadataPath = Path.Combine(dir, (string)pres["metadata"]);
            f.AudioPath = Path.Combine(dir, (string)pres["audio"]);
            f.Fps = pres.TryGetValue("fps", out var fps) ? MiniYaml.ToDouble(fps) : 0;
            f.Offset = pres.TryGetValue("offset", out var off) ? MiniYaml.ToDouble(off) : 0;

            // Channel order: bed channels first, then objects, exactly as listed.
            var byId = new Dictionary<int, DamfElement>();
            if (pres.TryGetValue("bedInstances", out var beds) && beds is List<object> bedList)
                foreach (Dictionary<string, object> bed in bedList)
                    foreach (Dictionary<string, object> ch in (List<object>)bed["channels"])
                        f.AddElement(byId, (int)MiniYaml.ToLong(ch["ID"]), true, (string)ch["channel"]);
            if (pres.TryGetValue("objects", out var objs) && objs is List<object> objList)
                foreach (Dictionary<string, object> o in objList)
                    f.AddElement(byId, (int)MiniYaml.ToLong(o["ID"]), false, null);

            var meta = (Dictionary<string, object>)MiniYaml.Parse(File.ReadAllText(f.MetadataPath));
            f.SampleRate = MiniYaml.ToDouble(meta["sampleRate"]);

            // Resolve the delta encoding: each element starts from defaults, and each event
            // overrides only the fields it states.
            var state = new Dictionary<int, DamfEvent>();
            foreach (var e in byId.Values)
                state[e.Id] = new DamfEvent { Active = false, GainDb = double.NegativeInfinity, Zones = "all", Elevation = true };

            foreach (Dictionary<string, object> ev in (List<object>)meta["events"])
            {
                int id = (int)MiniYaml.ToLong(ev["ID"]);
                if (!byId.TryGetValue(id, out var element))
                    throw new InvalidDataException("metadata event for ID " + id + ", which the .atmos header doesn't list");
                var s = state[id];
                s.SamplePos = MiniYaml.ToLong(ev["samplePos"]);
                if (ev.TryGetValue("active", out var v)) s.Active = MiniYaml.ToBool(v);
                if (ev.TryGetValue("pos", out v))
                {
                    var p = (List<object>)v;
                    s.X = MiniYaml.ToDouble(p[0]);
                    s.Y = MiniYaml.ToDouble(p[1]);
                    s.Z = MiniYaml.ToDouble(p[2]);
                }
                if (ev.TryGetValue("size", out v)) s.Size = MiniYaml.ToDouble(v);
                if (ev.TryGetValue("gain", out v)) s.GainDb = MiniYaml.ToDouble(v);
                if (ev.TryGetValue("rampLength", out v)) s.RampLength = MiniYaml.ToLong(v);
                if (ev.TryGetValue("snap", out v)) s.Snap = MiniYaml.ToBool(v);
                if (ev.TryGetValue("zones", out v)) s.Zones = (string)v;
                if (ev.TryGetValue("elevation", out v)) s.Elevation = MiniYaml.ToBool(v);
                state[id] = s;

                var list = element.Events;
                if (list.Count > 0 && s.SamplePos < list[list.Count - 1].SamplePos)
                    throw new InvalidDataException("events for ID " + id + " are not in time order");
                if (list.Count > 0 && s.SamplePos == list[list.Count - 1].SamplePos)
                    list[list.Count - 1] = s;   // same instant: the later line wins
                else
                    list.Add(s);
            }
            return f;
        }

        void AddElement(Dictionary<int, DamfElement> byId, int id, bool isBed, string bedChannel)
        {
            if (byId.ContainsKey(id)) throw new InvalidDataException("duplicate element ID " + id);
            var e = new DamfElement { Index = Elements.Count, Id = id, IsBed = isBed, BedChannel = bedChannel };
            Elements.Add(e);
            byId[id] = e;
        }

        /// <summary>The element's state at a sample position (the last event at or before it).</summary>
        public static int EventIndexAt(DamfElement e, long samplePos)
        {
            int lo = 0, hi = e.Events.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (e.Events[mid].SamplePos <= samplePos) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }
    }
}
