using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System;
using System.IO;
using System.Linq;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ToDHelperPlugin.Windows;
using ToDHelperPlugin.Data;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Chat;

namespace ToDHelperPlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName1 = "/tod";
    private const string CommandName2 = "/todhelper";

    public static readonly System.Collections.Generic.HashSet<string> WorldNames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] FallbackWorlds = new string[]
    {
        // EU
        "Cerberus", "Lich", "Odin", "Phoenix", "Shiva", "Twintania", "Zodiark", "Ragnarok", "Louisoix", "Spriggan", "Moogle", "Omega", "Phantom", "Sagittarius", "Alpha", "Raiden",
        // NA
        "Adamantoise", "Cactuar", "Gilgamesh", "Midgardsormr", "Siren", "Sargatanas", "Behemoth", "Excalibur", "Lamia", "Ultros", "Leviathan", "Hyperion", "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera", "Halicarnassus", "Maduin", "Marilith", "Seraph",
        // JP
        "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Valefor", "Yojimbo", "Zeromus", "Anima", "Asura", "Chocobo", "Hades", "Mandragora", "Masamune", "Shinryu", "Titan", "Belias", "Carbuncle", "Gungnir", "Kujata", "Ramuh", "Tonberry", "Typhon", "Unicorn", "Atomos", "Garuda", "Aegis",
        // OC
        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan"
    };

    public Configuration Configuration { get; init; }
    public GameState GameState { get; init; } = new();
    public InspirationManager InspirationManager { get; init; }

    public readonly WindowSystem WindowSystem = new("ToDHelperPlugin");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        // Initialize world names
        foreach (var w in FallbackWorlds)
        {
            WorldNames.Add(w);
        }
        try
        {
            var worldSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
            if (worldSheet != null)
            {
                foreach (var world in worldSheet)
                {
                    var name = world.Name.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        WorldNames.Add(name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load world names from DataManager");
        }

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Normalize any player names in SavedPlayers that contain server suffixes
        var migratedPlayers = new System.Collections.Generic.Dictionary<string, Data.PlayerStats>();
        foreach (var kvp in Configuration.SavedPlayers)
        {
            var normalizedKey = NormalizePlayerName(kvp.Key, "");
            kvp.Value.Name = normalizedKey;
            migratedPlayers[normalizedKey] = kvp.Value;
        }
        Configuration.SavedPlayers = migratedPlayers;
        GameState.Players = Configuration.SavedPlayers;

        foreach (var turn in Configuration.SavedTurnLog)
        {
            turn.Giver = NormalizePlayerName(turn.Giver, "");
            turn.Receiver = NormalizePlayerName(turn.Receiver, "");
        }
        GameState.TurnLog = Configuration.SavedTurnLog;
        InspirationManager = new InspirationManager(PluginInterface.ConfigDirectory.FullName);

        // Ensure default messages exist if lists are completely empty
        if (Configuration.RoundStartMessages.Count == 0)
            Configuration.RoundStartMessages.Add("A new round has started! You have [Timer] to roll!");
        if (Configuration.RoundReminderMessages.Count == 0)
            Configuration.RoundReminderMessages.Add("Hurry up! Only [RemainingTimer] left!");
        if (Configuration.RoundClosedMessages.Count == 0)
            Configuration.RoundClosedMessages.Add("Round [RoundNumber] closed! Thank you for participating.");
        if (Configuration.DiceAnnounceMessages.Count == 0)
            Configuration.DiceAnnounceMessages.Add("The dice have spoken! [GiverName] asks [ReceiverName]!");
        if (Configuration.CustomAnnounceMessages.Count == 0)
            Configuration.CustomAnnounceMessages.Add("Get ready! [CustomGiverName] asks [CustomReceiverName]!");

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName1, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the ToD Helper main window"
        });

        CommandManager.AddHandler(CommandName2, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the ToD Helper main window"
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
        
        ChatGui.ChatMessage += OnChatMessage;
        Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        CommandManager.RemoveHandler(CommandName1);
        CommandManager.RemoveHandler(CommandName2);
        
        ChatGui.ChatMessage -= OnChatMessage;
        Framework.Update -= OnUpdate;
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }
    
    private void OnChatMessage(IHandleableChatMessage message)
    {
        // Only monitor if Automatic Mode and Round is active
        if (!Configuration.AutomaticMode || !GameState.IsRoundActive)
            return;

        var type = message.LogKind;
        var sender = message.Sender;
        var msg = message.Message;

        // Mode 0: /random. FFXIV usually sends standard /random rolls as XivChatType.Standard or SystemMessage.
        // Mode 1: /dice. We filter by the user's selected channels.
        
        if (Configuration.RollMode == 0)
        {
            // Usually /random is public, we would check if it's a dice roll.
        }
        else if (Configuration.RollMode == 1)
        {
            // For /dice, we check if the channel matches the allowed ones.
            bool allowed = false;
            if (Configuration.WatchSay && type == XivChatType.Say) allowed = true;
            if (Configuration.WatchParty && (type == XivChatType.Party || type == XivChatType.CrossParty)) allowed = true;
            if (Configuration.WatchAlliance && type == XivChatType.Alliance) allowed = true;
            if (Configuration.WatchFC && type == XivChatType.FreeCompany) allowed = true;
            if (Configuration.WatchOthers)
            {
                // Linkshells and CrossWorldLinkshells are types 16-23 and 37-44 usually
                if (type >= XivChatType.Ls1 && type <= XivChatType.Ls8) allowed = true;
                if (type >= XivChatType.CrossLinkShell1 && type <= XivChatType.CrossLinkShell8) allowed = true;
            }
            
            if (!allowed) return;
        }

        string localWorld = "";
        if (ObjectTable.LocalPlayer != null)
        {
            localWorld = ObjectTable.LocalPlayer.HomeWorld.Value.Name.ToString();
        }

        // 1. Check for standard /random roll text:
        // E.g. "You roll a 59 (out of 100)." or "Player Name rolls a 59 (out of 100)."
        // or "Random! You roll a 🎲777." or "Random! Player Name rolls a 🎲59."
        var randomMatch = System.Text.RegularExpressions.Regex.Match(
            msg.TextValue, 
            @"(?:Random!\s+)?(?:(?<name>.+?)\s+rolls?|You roll)\s+a\s+[^\d]*(?<roll>\d+)", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (randomMatch.Success)
        {
            string playerName;
            if (randomMatch.Groups["name"].Success)
            {
                playerName = randomMatch.Groups["name"].Value;
            }
            else
            {
                playerName = ObjectTable.LocalPlayer?.Name.TextValue ?? ObjectTable[0]?.Name.TextValue ?? "You";
            }

            // Fallback: If playerName resolved to "You", try to get the local player's actual name
            if (playerName.Equals("You", StringComparison.OrdinalIgnoreCase))
            {
                var localName = ObjectTable.LocalPlayer?.Name.TextValue ?? ObjectTable[0]?.Name.TextValue;
                if (!string.IsNullOrEmpty(localName))
                {
                    playerName = localName;
                }
            }

            playerName = NormalizePlayerName(playerName, localWorld);

            if (int.TryParse(randomMatch.Groups["roll"].Value, out int parsedRoll))
            {
                GameState.AddRoll(playerName, parsedRoll);
            }
            return;
        }

        // 2. Check for /dice roll text:
        // In /dice, the game outputs to chat e.g. "Random! 🎲581" with auto-translate arrows (Random! 🎲581 )
        // and the player name is the sender of the chat message.
        var diceMatch = System.Text.RegularExpressions.Regex.Match(
            msg.TextValue, 
            @"(?:Random!|Zufallswert!|Au sort\s*!|Aléatoire\s*!|ランダム！|[^\w\d]*Random![^\w\d]*)\s*[^\d]*(?<roll>\d+)", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (diceMatch.Success)
        {
            string playerName = string.Empty;

            // Try PlayerPayload first if available in sender payloads
            if (sender != null && sender.Payloads != null)
            {
                var playerPayload = sender.Payloads.OfType<Dalamud.Game.Text.SeStringHandling.Payloads.PlayerPayload>().FirstOrDefault();
                if (playerPayload != null)
                {
                    var pWorld = playerPayload.World.Value.Name.ToString();
                    playerName = !string.IsNullOrEmpty(pWorld) ? $"{playerPayload.PlayerName}@{pWorld}" : playerPayload.PlayerName;
                }
            }

            // Fallback to sender.TextValue
            if (string.IsNullOrEmpty(playerName) && sender != null)
            {
                playerName = sender.TextValue;
            }

            // Fallback: If sender was somehow empty, try local player
            if (string.IsNullOrEmpty(playerName))
            {
                playerName = ObjectTable.LocalPlayer?.Name.TextValue ?? ObjectTable[0]?.Name.TextValue ?? "You";
            }

            playerName = NormalizePlayerName(playerName, localWorld);

            if (int.TryParse(diceMatch.Groups["roll"].Value, out int parsedRoll))
            {
                GameState.AddRoll(playerName, parsedRoll);
            }
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (!GameState.IsRoundActive) return;

        var now = System.DateTime.Now;
        var remaining = (GameState.RoundEndTime - now).TotalSeconds;

        if (remaining <= 0)
        {
            // End Round
            var roundNum = GameState.EndRound();
            Configuration.Save();
            if (Configuration.EnableRoundClosedMsg && Configuration.RoundClosedMessages.Count > 0)
            {
                var msg = Configuration.RoundClosedMessages[new System.Random().Next(Configuration.RoundClosedMessages.Count)];
                msg = msg.Replace("[RoundNumber]", roundNum.ToString());
                ChatSender.SendMessage(Configuration.ChatPrefix.Trim() + " " + msg);
            }
        }
        else if (!GameState.RoundReminderTriggered && remaining <= Configuration.RoundReminderTime)
        {
            GameState.RoundReminderTriggered = true;
            if (Configuration.EnableRoundReminderMsg && Configuration.RoundReminderMessages.Count > 0)
            {
                var msg = Configuration.RoundReminderMessages[new System.Random().Next(Configuration.RoundReminderMessages.Count)];
                msg = msg.Replace("[RemainingTimer]", $"{(int)Math.Ceiling(remaining)}s");
                msg = msg.Replace("[RoundNumber]", GameState.RoundsPlayed.ToString());
                ChatSender.SendMessage(Configuration.ChatPrefix.Trim() + " " + msg);
            }
        }
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    public static string CleanServerName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return string.Empty;

        fullName = fullName.Trim('<', '>', '[', ']', '(', ')', ' ');

        // 1. Split by standard separators
        char[] separators = new char[] { '@', '¤', '(', '[', '<' };
        var clean = fullName;
        foreach (var sep in separators)
        {
            if (clean.Contains(sep))
            {
                clean = clean.Split(sep)[0].Trim();
            }
        }

        // 2. Check if it ends with a valid world name directly concatenated
        foreach (var world in WorldNames)
        {
            if (clean.EndsWith(world, StringComparison.OrdinalIgnoreCase))
            {
                var potentialName = clean.Substring(0, clean.Length - world.Length).Trim();
                if (potentialName.Contains(" "))
                {
                    clean = potentialName;
                    break;
                }
            }
        }

        return clean;
    }

    public static string NormalizePlayerName(string fullName, string defaultWorld)
    {
        if (string.IsNullOrEmpty(fullName)) return string.Empty;
        if (fullName.Equals("You", StringComparison.OrdinalIgnoreCase)) return fullName;

        fullName = fullName.Trim('<', '>', '[', ']', '(', ')', ' ');

        char[] separators = new char[] { '@', '¤', '(', '[', '<' };
        foreach (var sep in separators)
        {
            if (fullName.Contains(sep))
            {
                var parts = fullName.Split(sep);
                var name = parts[0].Trim();
                var world = parts[1].Replace(")", "").Replace("]", "").Replace(">", "").Trim();

                // Clean concatenated server suffix from name if present
                foreach (var w in WorldNames)
                {
                    if (name.EndsWith(w, StringComparison.OrdinalIgnoreCase))
                    {
                        var potential = name.Substring(0, name.Length - w.Length).Trim();
                        if (potential.Contains(" "))
                        {
                            name = potential;
                            break;
                        }
                    }
                }
                return $"{name}@{world}";
            }
        }

        // Check if it ends with a valid world name directly concatenated
        foreach (var world in WorldNames)
        {
            if (fullName.EndsWith(world, StringComparison.OrdinalIgnoreCase))
            {
                var potentialName = fullName.Substring(0, fullName.Length - world.Length).Trim();
                if (potentialName.Contains(" "))
                {
                    return $"{potentialName}@{world}";
                }
            }
        }

        if (!string.IsNullOrEmpty(defaultWorld))
        {
            return $"{fullName}@{defaultWorld}";
        }
        return fullName;
    }
}
