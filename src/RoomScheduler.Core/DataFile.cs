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

            // Nothing found. Hand back the original so the caller reports a
            // missing file using the path the developer actually asked for.
            return relativePath;
        }
    }
}
