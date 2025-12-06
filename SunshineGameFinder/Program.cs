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

// Add admin check before any operations
if (!IsRunAsAdmin())
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
            Logger.Log($"This application requires administrative privileges. Elevation failed: {ex.Message}", LogLevel.Error);
            return;
        }
    }
}

// constants
const string steamLibraryFolders = ".steam/steam/steamapps/libraryfolders.vdf";

// default values
var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
sunshineConfigLocationOption.Description = "Specify the Sunshine apps.json location";
sunshineConfigLocationOption.AllowMultipleArgumentsPerToken = false;
sunshineConfigLocationOption.DefaultValueFactory = arg => @"/usr/share/sunshine/apps.json";
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
            // On Linux, look for executables without extension or with .sh extension
            var exe = Directory.GetFiles(gameDir.FullName, "*", SearchOption.AllDirectories)
                .Where(f => 
                {
                    var fileInfo = new FileInfo(f);
                    // Check if file is executable (has execute permission) or has common executable extensions
                    var name = fileInfo.Name.ToLower();
                    var isExecutable = fileInfo.Extension == "" || 
                                       fileInfo.Extension == ".sh" || 
                                       fileInfo.Extension == ".AppImage" ||
                                       name.EndsWith(".x86_64") ||
                                       name.EndsWith(".x86") ||
                                       name.EndsWith(".bin");
                    return isExecutable;
                })
                .FirstOrDefault(exefile =>
                {
                    var exeName = new FileInfo(exefile).Name.ToLower();
                    var exeNameWithoutExt = Path.GetFileNameWithoutExtension(exeName).ToLower();
                    var gameNameLower = gameName.ToLower();
                    var dirNameLower = gameDir.Name.ToLower();
                    return exeName == dirNameLower || 
                           exeName == gameNameLower || 
                           exeNameWithoutExt == dirNameLower ||
                           exeNameWithoutExt == gameNameLower ||
                           !exeExclusionWords.Any(ew => exeName.Contains(ew.ToLower()));
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
                string coversFolderPath = Path.GetFullPath(Path.Combine(sunshineRootFolder, "covers"));
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


