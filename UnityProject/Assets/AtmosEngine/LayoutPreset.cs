// Speaker layout presets, loaded from YAML files (StreamingAssets/Layouts/*.yaml) so anyone can
// add their own. See any of those files for the format.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AtmosEngine
{
    public sealed class PresetSpeaker
    {
        public string Name;          // shown in the 3D view, e.g. "Ltf"
        public string Label;         // BS.2051 label, e.g. "U+045"; used to route bed channels
        public double Azimuth;       // degrees, positive = left
        public double Elevation;     // degrees, positive = up
        public double X, Y, Z;       // room-box position used by the room-relative panner
        public bool IsLfe;
        public string Mount;         // "wall": an upper speaker on the wall (drawn there); otherwise in the ceiling
    }

    public sealed class LayoutPreset
    {
        public string Name { get; private set; }
        public string Description { get; private set; }
        public int Order { get; private set; }
        public string Bs2051 { get; private set; }        // null unless identical to a BS.2051 layout
        public string SourcePath { get; private set; }
        public List<PresetSpeaker> Speakers { get; } = new List<PresetSpeaker>();

        public int EarLevelCount => Speakers.Count(s => !s.IsLfe && s.Z < 0.5);
        public int OverheadCount => Speakers.Count(s => !s.IsLfe && s.Z >= 0.5);
        public int LfeCount => Speakers.Count(s => s.IsLfe);

        public static LayoutPreset Load(string path) => Parse(File.ReadAllText(path), path);

        public static LayoutPreset Parse(string yaml, string sourcePath = null)
        {
            var root = MiniYaml.Parse(yaml) as Dictionary<string, object>
                       ?? throw new InvalidDataException("layout file is empty");
            var p = new LayoutPreset
            {
                Name = Str(root, "name"),
                Description = root.TryGetValue("description", out var d) ? (string)d : "",
                Order = root.TryGetValue("order", out var o) ? (int)MiniYaml.ToLong(o) : 1000,
                Bs2051 = root.TryGetValue("bs2051", out var b) ? (string)b : null,
                SourcePath = sourcePath,
            };
            if (!(root.TryGetValue("speakers", out var list) && list is List<object> speakers) || speakers.Count == 0)
                throw new InvalidDataException(p.Name + ": no speakers listed");
            foreach (Dictionary<string, object> s in speakers)
            {
                var pos = (List<object>)s["position"];
                p.Speakers.Add(new PresetSpeaker
                {
                    Name = Str(s, "name"),
                    Label = Str(s, "label"),
                    Azimuth = s.TryGetValue("azimuth", out var az) ? MiniYaml.ToDouble(az) : 0,
                    Elevation = s.TryGetValue("elevation", out var el) ? MiniYaml.ToDouble(el) : 0,
                    X = MiniYaml.ToDouble(pos[0]),
                    Y = MiniYaml.ToDouble(pos[1]),
                    Z = MiniYaml.ToDouble(pos[2]),
                    IsLfe = s.TryGetValue("lfe", out var lfe) && MiniYaml.ToBool(lfe),
                    Mount = s.TryGetValue("mount", out var mount) ? mount as string : null,
                });
            }
            p.Validate();
            return p;
        }

        void Validate()
        {
            var dupName = Speakers.GroupBy(s => s.Name).FirstOrDefault(g => g.Count() > 1);
            if (dupName != null) throw new InvalidDataException(Name + ": two speakers named " + dupName.Key);
            var dupLabel = Speakers.GroupBy(s => s.Label).FirstOrDefault(g => g.Count() > 1);
            if (dupLabel != null) throw new InvalidDataException(Name + ": two speakers labelled " + dupLabel.Key);
            var dupPos = Speakers.Where(s => !s.IsLfe).GroupBy(s => (s.X, s.Y, s.Z)).FirstOrDefault(g => g.Count() > 1);
            if (dupPos != null) throw new InvalidDataException(Name + ": two speakers at the same position " + dupPos.First().Name);
            foreach (var s in Speakers.Where(s => !s.IsLfe))
                if (Math.Abs(s.X) > 1 || Math.Abs(s.Y) > 1 || Math.Abs(s.Z) > 1)
                    throw new InvalidDataException(Name + ": " + s.Name + " is outside the room box (-1..1)");
            if (!Speakers.Any(s => !s.IsLfe)) throw new InvalidDataException(Name + ": needs at least one non-LFE speaker");
        }

        /// <summary>The libear layout for this preset (used for bed channels). Channel names are
        /// the BS.2051 labels. When the preset says it is a BS.2051 layout, libear's own definition
        /// is used so its standard downmix rules apply; the speaker order must then match.</summary>
        public SpeakerLayout CreateSpeakerLayout()
        {
            if (!string.IsNullOrEmpty(Bs2051))
            {
                var layout = SpeakerLayout.Bs2051(Bs2051);
                if (!layout.ChannelNames.SequenceEqual(Speakers.Select(s => s.Label)))
                {
                    layout.Dispose();
                    throw new InvalidDataException(Name + ": speakers must be listed in BS.2051 " + Bs2051 + " order: "
                                                   + string.Join(" ", layout.ChannelNames));
                }
                return layout;
            }
            return SpeakerLayout.Custom(Name,
                Speakers.Select(s => s.Label).ToArray(),
                Speakers.Select(s => s.Azimuth).ToArray(),
                Speakers.Select(s => s.Elevation).ToArray(),
                Speakers.Select(s => s.IsLfe).ToArray());
        }

        /// <summary>x, y, z per speaker, in speaker order, for the room-relative panner.</summary>
        public double[] AllocentricPositions()
        {
            var a = new double[Speakers.Count * 3];
            for (int i = 0; i < Speakers.Count; i++)
            {
                a[3 * i] = Speakers[i].X;
                a[3 * i + 1] = Speakers[i].Y;
                a[3 * i + 2] = Speakers[i].Z;
            }
            return a;
        }

        /// <summary>All presets in a folder, sorted for display. Files that fail to load are
        /// reported through errors and skipped.</summary>
        public static List<LayoutPreset> LoadAll(string directory, List<string> errors = null)
        {
            var result = new List<LayoutPreset>();
            if (!Directory.Exists(directory)) return result;
            foreach (var path in Directory.GetFiles(directory, "*.yaml"))
            {
                try { result.Add(Load(path)); }
                catch (Exception e) { errors?.Add(Path.GetFileName(path) + ": " + e.Message); }
            }
            return result.OrderBy(p => p.Order).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        }

        static string Str(Dictionary<string, object> m, string key) =>
            m.TryGetValue(key, out var v) && v is string s && s.Length > 0
                ? s : throw new InvalidDataException("missing '" + key + "'");

        public override string ToString() => Name;
    }
}
