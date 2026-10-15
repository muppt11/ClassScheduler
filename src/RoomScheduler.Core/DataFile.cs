using System;
using System.IO;

namespace RoomScheduler
{
    /// <summary>
    /// Finds a data file whether the app was started from its project folder or
    /// from the folder it was built into. The console app and the web app have
    /// different working directories, so a bare relative path is not enough.
    /// </summary>
    public static class DataFile
    {
        public static string Resolve(string relativePath)
        {
            if (File.Exists(relativePath))
                return relativePath;

            string besideExecutable = Path.Combine(AppContext.BaseDirectory, relativePath);
            if (File.Exists(besideExecutable))
                return besideExecutable;

            string fromRoot = Path.Combine(Root(), relativePath);
            if (File.Exists(fromRoot))
                return fromRoot;

            // Nothing found. Hand back the original so the caller reports a
            // missing file using the path the developer actually asked for.
            return relativePath;
        }

        /// <summary>A data file's text, or "" if it is missing.</summary>
        public static string ReadText(string relativePath)
        {
            string path = Resolve(relativePath);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        /// <summary>
        /// The repository folder: the nearest folder above the working directory
        /// or the executable that has data/buildings.csv. Private department data
        /// (data/private) and saved state (data/local) live there and are never
        /// copied into build output.
        /// </summary>
        public static string Root()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "data", "buildings.csv")))
                        return dir.FullName;
                }
            }

            return Directory.GetCurrentDirectory();
        }
    }
}
