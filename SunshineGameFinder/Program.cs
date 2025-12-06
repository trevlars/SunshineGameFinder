// See https://aka.ms/new-console-template for more information
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using SunshineGameFinder;
using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

// Helper to find Sunshine config location (where Sunshine actually reads from)
// Note: This function is defined later after GetActualUserHomeDirectory, but declared here for early use
static string FindSunshineConfig()
{
    // Use a simple approach first - we'll refine this after GetActualUserHomeDirectory is available
    var currentHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
    var homeDir = !string.IsNullOrEmpty(sudoUser) ? 
        (Directory.Exists($"/home/{sudoUser}") ? $"/home/{sudoUser}" : 
         Directory.Exists($"/var/home/{sudoUser}") ? $"/var/home/{sudoUser}" : currentHome) : 
        currentHome;
    
    // First, check if sunshine.conf exists and has file_apps setting
    var sunshineConfPaths = new[]
    {
        Path.Combine(homeDir, ".config/sunshine/sunshine.conf"),
        "/etc/sunshine/sunshine.conf",
        "/usr/share/sunshine/sunshine.conf"
    };
    
    foreach (var confPath in sunshineConfPaths)
    {
        if (File.Exists(confPath))
        {
            try
            {
                var confContent = File.ReadAllText(confPath);
                // Look for file_apps = /path/to/apps.json
                var match = System.Text.RegularExpressions.Regex.Match(confContent, @"file_apps\s*=\s*(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var appsPath = match.Groups[1].Value.Trim().Trim('"', '\'');
                    if (File.Exists(appsPath))
                    {
                        return appsPath;
                    }
                }
            }
            catch
            {
                // Continue to next location if we can't read the config
            }
        }
    }
    
    // If no config file specifies it, check common locations where apps.json might exist
    var existingLocations = new[]
    {
        Path.Combine(homeDir, ".config/sunshine/apps.json"),  // Default Linux location
        "/etc/sunshine/apps.json",                            // System-wide config
        "/usr/share/sunshine/apps.json"                       // Package default (read-only on immutable systems)
    };
    
    foreach (var location in existingLocations)
    {
        if (File.Exists(location))
        {
            return location;
        }
    }
    
    // If no existing file found, default to user config directory (writable, no sudo needed)
    return Path.Combine(homeDir, ".config/sunshine/apps.json");
}

// Helper function to check if we can write to a file (or its directory if file doesn't exist)
static bool CanWriteToPath(string filePath)
{
    try
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory != null)
        {
            // Create directory if it doesn't exist (if we have permission)
            if (!Directory.Exists(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch
                {
                    return false;
                }
            }
            
            // Check if we can write to the directory by actually writing and deleting a test file
            var testFile = Path.Combine(directory, ".sunshine_game_finder_write_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch
            {
                // Try to clean up if write succeeded but delete failed
                try { File.Delete(testFile); } catch { }
                return false;
            }
        }
        else if (File.Exists(filePath))
        {
            // Check if we can write to the existing file by trying to open it for writing
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
    catch
    {
    }
    return false;
}

// Check if we need admin privileges - only request if we can't write to the config file
// Parse args first to get the config location
var tempRootCommand = new RootCommand();
var tempSunshineConfigLocationOption = new Option<string>("--sunshineConfigLocation", "-c");
tempSunshineConfigLocationOption.DefaultValueFactory = arg => FindSunshineConfig();
tempRootCommand.Options.Add(tempSunshineConfigLocationOption);
var tempParseResult = tempRootCommand.Parse(args);
var tempSunshineConfigLocation = tempParseResult.GetValue(tempSunshineConfigLocationOption) ?? FindSunshineConfig();

// Check if we need admin privileges
var needsAdmin = !CanWriteToPath(tempSunshineConfigLocation);

if (needsAdmin && !IsRunAsAdmin())
{
    // Restart program and run as admin
    var exeName = Process.GetCurrentProcess().MainModule?.FileName;
    if (exeName != null)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var processInfo = new ProcessStartInfo(exeName)
                {
                    UseShellExecute = true,
                    Verb = "runas",   // This triggers the UAC elevation prompt
                    Arguments = string.Join(" ", args)  // Pass along any command line arguments
                };

                Process.Start(processInfo);
            }
            else
            {
                var processInfo = new ProcessStartInfo("sudo")
                {
                    UseShellExecute = true  // Required for interactive sudo password prompt
                };
                // Preserve SUDO_USER and other environment variables
                var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
                var currentUser = Environment.UserName;
                if (string.IsNullOrEmpty(sudoUser) && currentUser != "root")
                {
                    processInfo.EnvironmentVariables["SUDO_USER"] = currentUser;
                }
                processInfo.ArgumentList.Add(exeName);
                foreach (var arg in args)
                {
                    processInfo.ArgumentList.Add(arg);
                }
                var process = Process.Start(processInfo);
                process?.WaitForExit();  // Wait for the elevated process to complete
            }

            return; // Exit this instance
        }
        catch (Exception ex)
        {
            // User declined the UAC prompt or sudo failed
            Logger.Log($"Cannot write to config file at {tempSunshineConfigLocation} and elevation failed: {ex.Message}", LogLevel.Error);
            Logger.Log($"You may need to run with sudo, or specify a config location you can write to with -c option", LogLevel.Error);
            return;
        }
    }
}

// constants
const string steamLibraryFolders = ".steam/steam/steamapps/libraryfolders.vdf";

// Helper function to get the actual user's home directory (even when running as sudo)
static string GetActualUserHomeDirectory()
{
    // Check SUDO_USER environment variable first (set when running with sudo)
    var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
    var currentUser = Environment.UserName;
    var currentHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    
    Logger.Log($"Current user: {currentUser}, Current home: {currentHome}, SUDO_USER: {sudoUser ?? "not set"}", LogLevel.Trace);
    
    if (!string.IsNullOrEmpty(sudoUser))
    {
        // Try to get home directory from /etc/passwd
        var homeFromPasswd = RunCommand("getent", $"passwd {sudoUser}");
        if (!string.IsNullOrEmpty(homeFromPasswd))
        {
            var parts = homeFromPasswd.Split(':');
            if (parts.Length >= 6)
            {
                var homeDir = parts[5];
                if (Directory.Exists(homeDir))
                {
                    Logger.Log($"Found home directory for {sudoUser}: {homeDir}", LogLevel.Trace);
                    return homeDir;
                }
            }
        }
        
        // Fallback: try common home directory locations
        var commonHomes = new[] { $"/home/{sudoUser}", $"/var/home/{sudoUser}" };
        foreach (var home in commonHomes)
        {
            if (Directory.Exists(home))
            {
                Logger.Log($"Found home directory for {sudoUser} at: {home}", LogLevel.Trace);
                return home;
            }
        }
    }
    
    // If running as root, try to find the actual user's home directory
    if (currentHome == "/root" && OperatingSystem.IsLinux())
    {
        // Check for other environment variables that might help
        var realUser = Environment.GetEnvironmentVariable("USER") ?? 
                      Environment.GetEnvironmentVariable("LOGNAME") ?? 
                      Environment.GetEnvironmentVariable("SUDO_USER");
        
        if (!string.IsNullOrEmpty(realUser) && realUser != "root")
        {
            var homeFromPasswd = RunCommand("getent", $"passwd {realUser}");
            if (!string.IsNullOrEmpty(homeFromPasswd))
            {
                var parts = homeFromPasswd.Split(':');
                if (parts.Length >= 6)
                {
                    var homeDir = parts[5];
                    if (Directory.Exists(homeDir))
                    {
                        Logger.Log($"Found home directory for {realUser}: {homeDir}", LogLevel.Trace);
                        return homeDir;
                    }
                }
            }
        }
        
        // Try to find the first non-root user's home directory with UID >= 1000
        var passwdOutput = RunCommand("getent", "passwd");
        if (!string.IsNullOrEmpty(passwdOutput))
        {
            var lines = passwdOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(':');
                if (parts.Length >= 6 && parts[0] != "root" && !string.IsNullOrEmpty(parts[0]))
                {
                    var uid = parts[2];
                    var homeDir = parts[5];
                    // Prefer UID >= 1000 (regular users) and existing directories
                    if (int.TryParse(uid, out var uidInt) && uidInt >= 1000 && Directory.Exists(homeDir))
                    {
                        Logger.Log($"Found home directory for user {parts[0]}: {homeDir}", LogLevel.Trace);
                        return homeDir;
                    }
                }
            }
        }
    }
    
    // Fallback to current user profile
    Logger.Log($"Using current home directory: {currentHome}", LogLevel.Trace);
    return currentHome;
}

static string RunCommand(string command, string arguments)
{
    try
    {
        var processInfo = new ProcessStartInfo(command)
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        using var process = Process.Start(processInfo);
        if (process != null)
        {
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return process.StandardOutput.ReadToEnd().Trim();
            }
        }
    }
    catch
    {
        // Command not found or failed, return empty
    }
    return string.Empty;
}

// default values
var homeDir = GetActualUserHomeDirectory();
var gameDirs = new HashSet<string>() 
{ 
    Path.Combine(homeDir, ".steam/steam/steamapps/common"),
    Path.Combine(homeDir, ".local/share/Epic/EpicGamesLauncher/ProgramData/Manifests"),
    Path.Combine(homeDir, ".local/share/Ubisoft Game Launcher/games")
};
var exclusionWords = new List<string>() { "Steam" };
var exeExclusionWords = new List<string>() { "Steam", "Cleanup", "DX", "Uninstall", "Touchup", "redist", "Crash", "Editor", "crs-handler" };

// command setup
RootCommand rootCommand = new RootCommand("Searches your computer for various common game install paths for the Sunshine application. After running it, all games that did not already exist will be added to the apps.json, meaning your Moonlight client should see them next time it is started.");
var addlDirectoriesOption = new Option<string[]>("--addlDirectories", "-d");
addlDirectoriesOption.AllowMultipleArgumentsPerToken = true;
addlDirectoriesOption.Description = "Additional platform directories to search. ONLY looks for game directories in the top level of this folder.";
rootCommand.Options.Add(addlDirectoriesOption);

var addlExeExclusionWordsOption = new Option<string[]>("--addlExeExclusionWords", "-exeExclude");
addlExeExclusionWordsOption.Description = "Additional words to exclude from exe names when searching for game executables.";
addlExeExclusionWordsOption.AllowMultipleArgumentsPerToken = true;
rootCommand.Options.Add(addlExeExclusionWordsOption);

var sunshineConfigLocationOption = new Option<string>("--sunshineConfigLocation", "-c");
sunshineConfigLocationOption.Description = "Specify the Sunshine apps.json location (default: auto-detected from sunshine.conf or ~/.config/sunshine/apps.json)";
sunshineConfigLocationOption.AllowMultipleArgumentsPerToken = false;
sunshineConfigLocationOption.DefaultValueFactory = arg => FindSunshineConfig();
rootCommand.Options.Add(sunshineConfigLocationOption);

var forceOption = new Option<bool>("--force", "-f");
forceOption.Description = "Force re-adding of existing games to Sunshine apps config";
forceOption.AllowMultipleArgumentsPerToken = false;
forceOption.DefaultValueFactory = arg => (false);
rootCommand.Options.Add(forceOption);

var removeUninstalledOption = new Option<bool>("--remove-uninstalled", "-ru");
removeUninstalledOption.AllowMultipleArgumentsPerToken = false;
removeUninstalledOption.Description = "Removes games from Sunshine apps config that no longer have an executable on disk";
removeUninstalledOption.DefaultValueFactory = arg => (false);
rootCommand.Options.Add(removeUninstalledOption);

var ensureDesktopAppOption = new Option<bool>("--ensure-desktop-app", "-desktop");
ensureDesktopAppOption.AllowMultipleArgumentsPerToken = false;
ensureDesktopAppOption.Description = "Ensures that the 'Desktop' app is there";
ensureDesktopAppOption.DefaultValueFactory = arg => (false);
rootCommand.Options.Add(ensureDesktopAppOption);

var ensureSteamBigPictureOption = new Option<bool>("--ensure-steam-big-picture", "-bigpicture");
ensureSteamBigPictureOption.AllowMultipleArgumentsPerToken = false;
ensureSteamBigPictureOption.Description = "Ensures that the 'Steam Big Picture' app is there";
ensureSteamBigPictureOption.DefaultValueFactory = arg => (false);
rootCommand.Options.Add(ensureSteamBigPictureOption);

var nowaitAfterRunning = new Option<bool>("--no-wait");
nowaitAfterRunning.AllowMultipleArgumentsPerToken = false;
nowaitAfterRunning.DefaultValueFactory = arg => (false);
rootCommand.Options.Add(nowaitAfterRunning);


Logger.Log($@"
Thanks for using the Sunshine Game Finder! App Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} - Runtime: {System.Environment.Version}

Searches your computer for various common game install paths for the Sunshine application. After running it, all games that did not already exist will be added to the apps.json, meaning your Moonlight client should see them next time it is started.

Have an issue or an idea? Come contribute at https://github.com/JMTK/SunshineGameFinder
");

ParseResult parseResult = rootCommand.Parse(args);

// options handler
var addlDirectories = parseResult.GetValue(addlDirectoriesOption);
var addlExeExclusionWords = parseResult.GetValue(addlExeExclusionWordsOption);
var sunshineConfigLocation = parseResult.GetValue(sunshineConfigLocationOption);
var forceUpdate = parseResult.GetValue(forceOption);
var removeUninstalled = parseResult.GetValue(removeUninstalledOption);
var ensureDesktop = parseResult.GetValue(ensureDesktopAppOption);
var ensureSteamBigPicture = parseResult.GetValue(ensureSteamBigPictureOption);
var nowait = parseResult.GetValue(nowaitAfterRunning);

foreach (var dir in addlDirectories)
{
    if (Directory.Exists(dir))
    {
        gameDirs.Add(dir);
    }
}
exeExclusionWords.AddRange(addlExeExclusionWords);
var sunshineAppsJson = sunshineConfigLocation;
var sunshineRootFolder = Path.GetDirectoryName(sunshineAppsJson);

if (!File.Exists(sunshineAppsJson))
{
    Logger.Log($"Could not find Sunshine Apps config at specified path: {sunshineAppsJson}", LogLevel.Error);
    return;
}
var sunshineAppInstance = JsonSerializer.Deserialize<SunshineConfig>(File.ReadAllText(sunshineAppsJson), SourceGenerationContext.Default.SunshineConfig);

sunshineAppInstance ??= new SunshineConfig() { Env = new Env() };
sunshineAppInstance.apps ??= new List<SunshineApp>();
sunshineAppInstance.Env ??= new Env();

var gamesAdded = 0;
var gamesRemoved = 0;
if (removeUninstalled)
{
    for (int i = sunshineAppInstance.apps.Count() - 1; i >= 0; i--) //keep tolist so we can remove elements while iterating on the "copy"
    {
        var existingApp = sunshineAppInstance.apps[i];
        if (existingApp != null)
        {
            var exeStillExists = existingApp.Cmd == null && existingApp.Detached == null ||
                                 existingApp.Cmd != null && File.Exists(existingApp.Cmd) ||
                                 existingApp.Cmd?.StartsWith("steam://") == true ||
                                 existingApp.Detached != null && existingApp.Detached.Any(detachedCommand =>
                                 {
                                     return detachedCommand == null ||
                                      !detachedCommand.Contains("exe") ||
                                      detachedCommand != null && detachedCommand.EndsWith("exe") && File.Exists(detachedCommand);
                                 });
            if (!exeStillExists)
            {
                Logger.Log($"{existingApp.Name} no longer has an exe, removing from apps config...",
                    LogLevel.Error);
                sunshineAppInstance.apps.RemoveAt(i);
                gamesRemoved++;
            }
        }
    }
}

if (sunshineAppInstance == null)
{
    Logger.Log($"Sunshine app list is null", LogLevel.Error);
    return;
}

var foldersScanned = new HashSet<string>();
async Task ScanFolder(string folder)
{
    if (folder == null)
        return;

    if (foldersScanned.Contains(folder))
        return;

    foldersScanned.Add(folder);
    Logger.Log($"Scanning for games in {folder}...");
    var di = new DirectoryInfo(folder);
    if (!di.Exists)
    {
        Logger.Log($"Directory for platform {di.Name} does not exist, skipping...", LogLevel.Warning);
        return;
    }
    foreach (var gameDir in di.GetDirectories())
    {
        try
        {
            Logger.Log($"\tLooking in {gameDir.FullName.Replace(folder, "")}...", false);
            var gameName = CleanGameName(gameDir.Name);
            if (exclusionWords.Any(ew => gameName.Contains(ew)))
            {
                Logger.Log($"Skipping due to excluded word match", LogLevel.Trace);
                continue;
            }
            // On Linux, look for actual executables
            var exe = Directory.GetFiles(gameDir.FullName, "*", SearchOption.AllDirectories)
                .Where(f => 
                {
                    var fileInfo = new FileInfo(f);
                    var name = fileInfo.Name.ToLower();
                    var path = f.ToLower();
                    
                    // Exclude common non-executable file types and data directories
                    var excludedExtensions = new[] { ".bin", ".dll", ".so", ".dylib", ".a", ".lib", ".dat", ".txt", ".json", ".xml", ".lua", ".asset", ".unity", ".meta", ".shader", ".cginc", ".hlsl", ".cg", ".cs", ".bundle", ".framework", ".pak", ".pck", ".exe.pck", ".license", ".mimetype" };
                    if (excludedExtensions.Any(ext => name.EndsWith(ext)))
                    {
                        return false;
                    }
                    
                    // Exclude specific non-executable file names
                    var excludedFileNames = new[] { "license", "readme", "changelog", "bootfile", "shader_list", "globalgamemanagers", "metadata", "mimetype", "tessellationtable" };
                    if (excludedFileNames.Any(excluded => name == excluded || name.EndsWith("/" + excluded)))
                    {
                        return false;
                    }
                    
                    // Exclude files in data/content directories (these are not executables)
                    var dataDirs = new[] { "/data/", "/_data/", "/content/", "/resources/", "/assets/", "/binaries/win", "/binaries/mac", "/engine/", "/tagame/", "/scripts/", "/level", "/cooked", "/renderer/", "/crash_reports/", "/streamingassets/" };
                    if (dataDirs.Any(dir => path.Contains(dir)))
                    {
                        return false;
                    }
                    
                    // Check for common executable extensions (must be in root or binaries directory)
                    var executableExtensions = new[] { ".sh", ".appimage", ".run", ".exe" };
                    if (executableExtensions.Any(ext => name.EndsWith(ext)))
                    {
                        // Only accept .exe files if they're in the root or a binaries directory (not in data folders)
                        if (name.EndsWith(".exe"))
                        {
                            var parentDir = Path.GetDirectoryName(path)?.ToLower() ?? "";
                            if (parentDir.EndsWith(gameDir.Name.ToLower()) || 
                                parentDir.Contains("/binaries/") ||
                                parentDir.Contains("/bin/"))
                            {
                                return true;
                            }
                            return false;
                        }
                        return true;
                    }
                    
                    // Check if filename matches game name (likely the main executable) - must be .exe or in root/binaries
                    var exeNameWithoutExt = Path.GetFileNameWithoutExtension(name);
                    var gameNameLower = gameName.ToLower();
                    var dirNameLower = gameDir.Name.ToLower();
                    if (exeNameWithoutExt == gameNameLower || exeNameWithoutExt == dirNameLower)
                    {
                        // Must be .exe file or in a binaries/linux directory
                        if (name.EndsWith(".exe") || path.Contains("/binaries/linux") || path.Contains("/binaries/x86_64") || path.Contains("/bin/"))
                        {
                            return true;
                        }
                    }
                    
                    // Files without extension in root or Binaries directory might be executables (but be very careful)
                    if (fileInfo.Extension == "" && (path.EndsWith("/" + name) || path.Contains("/binaries/linux") || path.Contains("/binaries/x86_64")))
                    {
                        // Additional check: filename should look like an executable (not a data file)
                        if (!excludedFileNames.Any(excluded => name == excluded) && name.Length > 2)
                        {
                            return true;
                        }
                    }
                    
                    return false;
                })
                .OrderByDescending(f => 
                {
                    // Prioritize files that match the game name and are in likely executable locations
                    var fileName = Path.GetFileNameWithoutExtension(f).ToLower();
                    var gameNameLower = gameName.ToLower();
                    var dirNameLower = gameDir.Name.ToLower();
                    var path = f.ToLower();
                    
                    int score = 0;
                    if (fileName == gameNameLower || fileName == dirNameLower) score += 10;
                    if (fileName.Contains(gameNameLower) || fileName.Contains(dirNameLower)) score += 5;
                    if (path.Contains("/binaries/linux") || path.Contains("/binaries/x86_64")) score += 3;
                    if (!path.Contains("/data/") && !path.Contains("/content/")) score += 2;
                    return score;
                })
                .FirstOrDefault(exefile =>
                {
                    var exeName = new FileInfo(exefile).Name.ToLower();
                    return !exeExclusionWords.Any(ew => exeName.Contains(ew.ToLower()));
                });
            if (string.IsNullOrEmpty(exe))
            {
                Logger.Log($"EXE not be found", LogLevel.Warning);
                continue;
            }

            var existingApp = sunshineAppInstance.apps?.FirstOrDefault(g => g.Cmd == exe || g.Name == gameName);
            if (forceUpdate || existingApp == null)
            {
                if (forceUpdate && existingApp != null)
                {
                    sunshineAppInstance.apps.Remove(existingApp);
                }
                if (exe.Contains("gamelaunchhelper") || exe.Contains("gamelaunchhelper.exe"))
                {
                    //xbox game pass game
                    existingApp = new SunshineApp()
                    {
                        Name = gameName,
                        Detached = new List<string>()
                        {
                            exe
                        },
                        WorkingDir = ""
                    };
                }
                else
                {
                    existingApp = new SunshineApp()
                    {
                        Name = gameName,
                        Cmd = exe,
                        WorkingDir = ""
                    };
                }
                // Try to use covers directory, but fallback to a writable location if needed
                string coversFolderPath = Path.GetFullPath(Path.Combine(sunshineRootFolder, "covers"));
                if (!CanWriteToPath(coversFolderPath))
                {
                    // Use user's home directory for covers if system directory is read-only
                    var homeDir = GetActualUserHomeDirectory();
                    coversFolderPath = Path.Combine(homeDir, ".local/share/sunshine/covers");
                }
                string fullPathOfCoverImage = await ImageScraper.SaveIGDBImageToCoversFolder(gameName, coversFolderPath);
                if (!string.IsNullOrEmpty(fullPathOfCoverImage))
                {
                    existingApp.ImagePath = fullPathOfCoverImage;
                }
                else
                {
                    Logger.Log("Failed to find cover image for " + gameName, LogLevel.Warning);
                }
                gamesAdded++;
                Logger.Log($"Adding new game to Sunshine apps: {gameName} - {exe}", LogLevel.Success);
                sunshineAppInstance.apps.Add(existingApp);
            }
            else
            {
                Logger.Log($"Found existing Sunshine app for {gameName} already!: " + (existingApp.Cmd ?? existingApp.Detached?.FirstOrDefault() ?? existingApp.Name).Trim());
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex.Message, LogLevel.Error);
        }
    }
    Console.WriteLine(""); //blank line to separate platforms
}

// Check for Steam libraryfolders.vdf in home directory
var libraryFoldersPath = Path.Combine(homeDir, steamLibraryFolders);
var file = new FileInfo(libraryFoldersPath);
if (file.Exists)
{
    try
    {
        var libraries = VdfConvert.Deserialize(File.ReadAllText(libraryFoldersPath));
        foreach (var library in libraries.Value)
        {
            if (library is not VProperty libProp)
                continue;
            try
            {
                Logger.Log("Found VDF library: " + libProp.Value.ToList().Select(v => v.Value<string>()));
            }
            catch (Exception ex)
            {
                Logger.Log("Failed to parse VDF library value: '" + ex.Message + "' at " + libraryFoldersPath, LogLevel.Warning);
            }

            var steamPath = libProp.Value.Value<string>("path");
            if (!string.IsNullOrEmpty(steamPath))
            {
                gameDirs.Add(Path.Combine(steamPath, "steamapps/common"));
            }
        }
    }
    catch (Exception vdfException)
    {
        Logger.Log("Failed to parse libraryfolders.vdf: '" + vdfException.Message + "' at " + libraryFoldersPath, LogLevel.Warning);
    }
}
else
{
    Logger.Log($"libraryfolders.vdf not found at {libraryFoldersPath}, skipping...", LogLevel.Warning);
}

foreach (var platformDir in gameDirs)
{
    await ScanFolder(platformDir);
}

if (ensureDesktop && !sunshineAppInstance.apps.Any(app => app.Name == "Desktop"))
{
    sunshineAppInstance.apps.Add(new DesktopApp());
}
if (ensureSteamBigPicture && !sunshineAppInstance.apps.Any(app => app.Name == "Steam Big Picture" || app.Cmd == "steam://open/bigpicture"))
{
    sunshineAppInstance.apps.Add(new SteamBigPictureApp());
}

Logger.Log("Finding Games Completed");
if (gamesAdded > 0 || gamesRemoved > 0)
{
    if (FileWriter.UpdateConfig(sunshineAppsJson, sunshineAppInstance))
    {
        Logger.Log($"Apps config is updated! {gamesAdded} apps were added. {gamesRemoved} apps were removed. Check Sunshine to ensure all games were added.", LogLevel.Success);
    }
}
else
{
    Logger.Log("No new games were found to be added to Sunshine");
}

if (!nowait)
{
    // Prompt the user to optionally restart the Sunshine service
    Console.Write("\nWould you like to restart the Sunshine service now? (Y/N): ");
    var restartResponse = Console.ReadLine();
    if (!string.IsNullOrEmpty(restartResponse) && restartResponse.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase))
    {
        Logger.Log("Attempting to restart Sunshine service...", LogLevel.Trace);
        await RestartSunshineServiceAsync();
    }
}

if (!nowait)
{
    Logger.Log("\nPress any key to exit...");
    Console.ReadKey();
}

string CleanGameName(string name)
{
    string[] toReplace = new string[] { "Win10", "Windows 10", "Win11", "Windows 11" };
    foreach (string toRemove in toReplace)
    {
        name = name.Replace(toRemove, "");
    }

    return name.Trim();
}

static bool IsRunAsAdmin()
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        else
        {
            // On Unix systems, check if the current user is root
            return Environment.UserName == "root";
        }
    }
    catch
    {
        return false;
    }
}

static async Task RestartSunshineServiceAsync()
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command \"Restart-Service -Name 'SunshineService' -Force\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc.WaitForExit();
            var outText = proc.StandardOutput.ReadToEnd();
            var errText = proc.StandardError.ReadToEnd();
            if (proc.ExitCode == 0)
            {
                Logger.Log("SunshineService restarted successfully.", LogLevel.Success);
            }
            else
            {
                Logger.Log($"Failed to restart SunshineService. ExitCode={proc.ExitCode}. {errText}", LogLevel.Warning);
            }
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo("systemctl", "restart sunshine")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc.WaitForExit();
            var outText = proc.StandardOutput.ReadToEnd();
            var errText = proc.StandardError.ReadToEnd();
            if (proc.ExitCode == 0)
            {
                Logger.Log("sunshine service restarted successfully.", LogLevel.Success);
            }
            else
            {
                Logger.Log($"Failed to restart sunshine service. ExitCode={proc.ExitCode}. {errText}", LogLevel.Warning);
            }
        }
        else
        {
            Logger.Log("Unsupported OS for restarting service", LogLevel.Warning);
        }
    }
    catch (Exception ex)
    {
        Logger.Log("Error restarting service: " + ex.Message, LogLevel.Error);
    }
}


