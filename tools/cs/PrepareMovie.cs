// Runs the app's movie preparation (AtmosEngine.MoviePreparer) from the command line and prints
// its progress - the same code the viewer runs when you open an MKV.
//
//   PrepareMovie <movie.mkv> [cacheRoot]
// cacheRoot defaults to %LOCALAPPDATA%\AtmosVisualizer\Cache, the app's own cache.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using AtmosEngine;

static class PrepareMovie
{
    static int Main(string[] args)
    {
        string cache = args.Length > 1 ? args[1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtmosVisualizer", "Cache");
        // the tools that come with the app, as the app finds them (this runs from tools/cs/bin)
        var tools = ExternalTools.Find(null, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "UnityProject", "Assets", "StreamingAssets", "Tools")));
        Console.WriteLine("tools: ffmpeg={0}\n       ffprobe={1}\n       mkvextract={2}\n       harletty={3}",
            tools.Ffmpeg, tools.Ffprobe, tools.Mkvextract, tools.Harletty);
        var prep = new MoviePreparer(args[0], cache, tools);
        var t0 = DateTime.UtcNow;
        prep.Start();
        var done = new int[prep.Steps.Count];
        string last = "";
        while (!prep.Finished)
        {
            Thread.Sleep(1000);
            string line = string.Join(" | ", prep.Steps.Select(s => s.Name.Split(' ')[0] + " " + State(s)));
            if (line != last) Console.WriteLine("{0,6:F0}s  {1}", (DateTime.UtcNow - t0).TotalSeconds, line);
            last = line;
            for (int i = 0; i < prep.Steps.Count; i++)
                if (prep.Steps[i].State >= PrepStep.Done && done[i] == 0)
                {
                    done[i] = 1;
                    Console.WriteLine("        step '{0}' finished at {1:F0}s: {2}", prep.Steps[i].Name, (DateTime.UtcNow - t0).TotalSeconds, prep.Steps[i].Detail);
                }
        }
        Console.WriteLine("total {0:F0}s; cache: {1}", (DateTime.UtcNow - t0).TotalSeconds, prep.CacheDir);
        Console.WriteLine("atmos: {0}\nvideo: decoded live from {1} ({2})", prep.AtmosPath, prep.Info.SourcePath, prep.Info.VideoSummary);
        if (prep.Error != null) Console.WriteLine("ERROR: " + prep.Error);
        return prep.Error == null ? 0 : 1;
    }

    static string State(PrepStep s)
    {
        switch (s.State)
        {
            case PrepStep.Running: return (s.Progress * 100).ToString("0") + "%";
            case PrepStep.Done: return "done";
            case PrepStep.Failed: return "FAILED";
            case PrepStep.Skipped: return "skipped";
            default: return "waiting";
        }
    }
}
