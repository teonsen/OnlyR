﻿using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Fclp.Internals.Extensions;
using Serilog;

namespace OnlyR.Utils
{
    /// <summary>
    /// General file / folder utilities
    /// </summary>
    public static class FileUtils
    {
        //private const string AppNamePathSegment = "OnlyR";

        private const string OptionsFileName = "options.json";

        //private const string FullDateFormat = "yyyy-MM-dd";

        /// <summary>
        /// Creates directory if it doesn't exist. Throws if cannot be created
        /// </summary>
        /// <param name="folderPath">Directory to create</param>
        public static void CreateDirectory(string folderPath)
        {
            if(folderPath.IsNullOrEmpty())
                throw new ArgumentNullException(nameof(folderPath));

            if (Directory.Exists(folderPath)) return;

            Directory.CreateDirectory(folderPath!);
            if (!Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException($"{Properties.Resources.CREATE_FOLDER_ERROR} - {folderPath}");
            }
        }

        ///// <summary>
        ///// Gets system temp folder
        ///// </summary>
        ///// <returns>Temp folder</returns>
        //public static string GetSystemTempFolder()
        //{
        //    return Path.GetTempPath();
        //}

        /// <summary>
        /// Gets number of free bytes available on the drive on which folderName resides
        /// </summary>
        /// <param name="folderName">A reference folder</param>
        /// <param name="freespace">Free space in bytes</param>
        /// <returns>True if the space could be calculated</returns>
        // ReSharper disable once UnusedMember.Global
        public static bool DriveFreeBytes(string folderName, out ulong freespace)
        {
            freespace = 0;

            if (!folderName.EndsWith("\\", StringComparison.Ordinal))
            {
                folderName += '\\';
            }

            if (NativeMethods.GetDiskFreeSpaceEx(folderName, out var free, out var _, out var _))
            {
                freespace = free;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the log folder
        /// </summary>
        /// <returns>Log folder</returns>
        public static string GetLogFolder()
        {
            //return Path.Combine(
            //    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            //    AppNamePathSegment,
            //    "Logs");
            return Path.Combine(Environment.CurrentDirectory, "Logs");
        }

        ///// <summary>
        ///// Gets the application's MyDocs folder, e.g. "...MyDocuments\OnlyR"
        ///// </summary>
        ///// <returns>Folder path</returns>
        //public static string GetDefaultMyDocsDestinationFolder()
        //{
        //    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppNamePathSegment);
        //}

        /// <summary>
        /// Gets the recording destination folder
        /// </summary>
        /// <param name="dt">Date of recording</param>
        /// <param name="commandLineIdentifier">Optional command-line id</param>
        /// <param name="rootFromOptions">Overriding root folder as manually specified in app's settings</param>
        /// <returns>Folder path</returns>
        public static string GetDestinationFolder2(string? commandLineIdentifier, string? rootFromOptions)
        {
            return Path.Combine(GetRootDestinationFolder(commandLineIdentifier, rootFromOptions), "Recordings");
        }

        ///// <summary>
        ///// Gets the recording destination folder
        ///// </summary>
        ///// <param name="dt">Date of recording</param>
        ///// <param name="commandLineIdentifier">Optional command-line id</param>
        ///// <param name="rootFromOptions">Overriding root folder as manually specified in app's settings</param>
        ///// <returns>Folder path</returns>
        //public static string GetDestinationFolder(DateTime dt, string? commandLineIdentifier, string? rootFromOptions)
        //{
        //    return Path.Combine(
        //        GetMonthlyDestinationFolder(dt, commandLineIdentifier, rootFromOptions),
        //        dt.ToString(FullDateFormat, CultureInfo.CurrentCulture));
        //}

        ///// <summary>
        ///// Gets the recording destination folder for the month
        ///// </summary>
        ///// <param name="dt">Date of recording</param>
        ///// <param name="commandLineIdentifier">Optional command-line id</param>
        ///// <param name="rootFromOptions">Overriding root folder as manually specified in app's settings</param>
        ///// <returns>Folder path</returns>
        //public static string GetMonthlyDestinationFolder(DateTime dt, string? commandLineIdentifier, string? rootFromOptions)
        //{
        //    return Path.Combine(
        //        GetRootDestinationFolder(commandLineIdentifier, rootFromOptions),
        //        dt.ToString("yyyy", CultureInfo.CurrentCulture),
        //        dt.ToString("MM", CultureInfo.CurrentCulture));
        //}

        //public static int? ParseYearFromFolderName(string yearlyDestinationFolderName)
        //{
        //    if (yearlyDestinationFolderName.Length != 4)
        //    {
        //        return null;
        //    }

        //    if (!int.TryParse(yearlyDestinationFolderName, out var result))
        //    {
        //        return null;
        //    }

        //    if (result < 2000 || result > 3000)
        //    {
        //        return null;
        //    }

        //    return result;
        //}

        //public static int? ParseMonthFromFolderName(string monthlyDestinationFolderName)
        //{
        //    if (monthlyDestinationFolderName.Length != 2)
        //    {
        //        return null;
        //    }

        //    if (!int.TryParse(monthlyDestinationFolderName, out var result))
        //    {
        //        return null;
        //    }

        //    if (result < 1 || result > 12)
        //    {
        //        return null;
        //    }

        //    return result;
        //}

        //public static DateTime? ParseDateFromFolderName(
        //    string fullDateDestinationFolderName, 
        //    int expectedYear,
        //    int expectedMonth)
        //{
        //    if (fullDateDestinationFolderName.Length != FullDateFormat.Length)
        //    {
        //        return null;
        //    }

        //    if (!DateTime.TryParseExact(
        //        fullDateDestinationFolderName,
        //        FullDateFormat,
        //        CultureInfo.InvariantCulture,
        //        DateTimeStyles.None,
        //        out var result))
        //    {
        //        return null;
        //    }

        //    if (result.Year != expectedYear || result.Month != expectedMonth)
        //    {
        //        return null;
        //    }

        //    return result.Date;
        //}

        /// <summary>
        /// Gets the recording destination folder
        /// </summary>
        /// <param name="commandLineIdentifier">Optional command-line id</param>
        /// <param name="rootFromOptions">Overriding root folder as manually specified in app's settings</param>
        /// <returns>Folder path</returns>
        public static string GetRootDestinationFolder(string? commandLineIdentifier, string? rootFromOptions)
        {
            return Path.Combine(
                GetRecordingFolderRoot(rootFromOptions),
                commandLineIdentifier ?? string.Empty);
        }

        /// <summary>
        /// Gets the temp recording folder (this is where recording files are initially created)
        /// </summary>
        /// <returns>Folder path</returns>
        public static string GetTempRecordingFolder()
        {
            //var folder = Path.Combine(GetSystemTempFolder(), AppNamePathSegment, "Recordings");
            var folder = Path.Combine(Environment.CurrentDirectory, "Temp");
            CreateDirectory(folder);
            return folder;
        }

        ///// <summary>
        ///// Gets the file path for storing the user options
        ///// </summary>
        ///// <param name="commandLineIdentifier">Optional command-line id</param>
        ///// <param name="optionsVersion">The options schema version</param>
        ///// <returns>Options file path.</returns>
        //public static string GetUserOptionsFilePath(string? commandLineIdentifier, int optionsVersion)
        //{
        //    return Path.Combine(
        //        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        //        AppNamePathSegment,
        //        commandLineIdentifier ?? string.Empty,
        //        optionsVersion.ToString(CultureInfo.CurrentCulture),
        //        OptionsFileName);
        //}

        /// <summary>
        /// Gets the file path for storing the user options
        /// </summary>
        /// <param name="commandLineIdentifier">Optional command-line id</param>
        /// <param name="optionsVersion">The options schema version</param>
        /// <returns>Options file path.</returns>
        public static string GetUserOptionsFilePath(string? commandLineIdentifier, int optionsVersion)
        {
            return Path.Combine(
                Environment.CurrentDirectory,
                "Options",
                commandLineIdentifier ?? string.Empty,
                optionsVersion.ToString(CultureInfo.CurrentCulture),
                OptionsFileName);
        }

        public static void SafeDeleteFolder(string candidate)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                return;
            }

            try
            {
                Directory.Delete(candidate);
            }
            catch (DirectoryNotFoundException)
            {
                Log.Logger.Error($"Trying to delete folder but not found {candidate}");
            }
            catch (UnauthorizedAccessException)
            {
                Log.Logger.Error($"No permission to delete folder {candidate}");
            }
            catch (IOException)
            {
                Log.Logger.Error($"Trying to delete folder but may be in use {candidate}");
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, $"Could not delete folder {candidate}");
            }
        }

        public static void SafeDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (DirectoryNotFoundException)
            {
                Log.Logger.Error($"Trying to delete file but folder not found {path}");
            }
            catch (UnauthorizedAccessException)
            {
                Log.Logger.Error($"No permission to delete file {path}");
            }
            catch (IOException)
            {
                Log.Logger.Error($"Trying to delete file but may be in use {path}");
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, $"Could not delete file {path}");
            }
        }

        public static bool IsDirectoryEmpty(string path)
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }

        public static string FindSuitableRecordingFolderToShow(string? commandLineIdentifier, string? destFolder)
        {
            string? folder = null;

            try
            {
                //var today = DateTime.Today;
                
                // first try today's folder...
                //folder = GetDestinationFolder(today, commandLineIdentifier, destFolder);
                folder = GetDestinationFolder2(commandLineIdentifier, destFolder);

                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);

                    //// try this month's folder...
                    //folder = GetMonthlyDestinationFolder(today, commandLineIdentifier, destFolder);

                    //if (!Directory.Exists(folder))
                    //{
                    //    folder = GetRootDestinationFolder(commandLineIdentifier, destFolder);

                    //    if (!Directory.Exists(folder) && !string.IsNullOrEmpty(commandLineIdentifier))
                    //    {
                    //        folder = GetRootDestinationFolder(string.Empty, destFolder);

                    //        if (!Directory.Exists(folder))
                    //        {
                    //            Directory.CreateDirectory(folder);
                    //        }
                    //    }
                    //}
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, $"Could not find destination folder {folder}");
            }

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                folder = Environment.CurrentDirectory;
                Directory.CreateDirectory(folder);
            }

            return folder;
        }

        public static void MoveFile(string sourcePath, string destPath)
        {
            if (File.Exists(sourcePath))
            {
                Log.Logger.Information($"Copying {sourcePath} to {destPath}");

                var path = Path.GetDirectoryName(destPath);
                if (path != null)
                {
                    Directory.CreateDirectory(path);
                    File.Move(sourcePath, destPath);
                }
            }
            else
            {
                Log.Logger.Information($"Copy ERROR! Source .mp3 does not exist. Path: {sourcePath}");
            }
        }

        private static string GetRecordingFolderRoot(string? rootFromOptions)
        {
            //return DirectoryIsAvailable(rootFromOptions) 
            //    ? rootFromOptions!
            //    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppNamePathSegment);
            return Environment.CurrentDirectory;
        }

        private static bool DirectoryIsAvailable(string? dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                return Directory.Exists(dir);
            }

            return true;
        }
    }
}
