using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace KnifeCopier;

public sealed partial class KnifeCopier : IModSharpModule
{
    private const int VTableGetCustomPaintKitIndex = 3;

    private const ushort DefaultKnifeCt = (ushort) EconItemId.KnifeCt;
    private const ushort DefaultKnifeTe = (ushort) EconItemId.KnifeTe;

    private const string KnifeNameColor = "#EB4B4B";

    private static readonly bool DebugMode = false;

    private const string GiveNamedItemGameDataKey = "CCSPlayer_ItemServices::GiveNamedItem";

    private const string LocaleFileName          = "knifecopier";
    private const string DefaultLanguage         = "english";
    private const string CommandName             = "knifecopy";
    private const string MenuManagerAssemblyName = "Sharp.Modules.MenuManager";

    private static readonly CStrikeTeam[] PlayableTeams = [CStrikeTeam.TE, CStrikeTeam.CT];

    private static readonly LoadoutSlot[] DebugSlots =
    [
        LoadoutSlot.Secondary0, LoadoutSlot.Secondary1, LoadoutSlot.Secondary2,
        LoadoutSlot.Secondary3, LoadoutSlot.Secondary4, LoadoutSlot.Secondary5,
    ];

    private static readonly Dictionary<string, string> CultureToSteamLang = new (StringComparer.OrdinalIgnoreCase)
    {
        ["pt-BR"]  = "brazilian",
        ["bg-BG"]  = "bulgarian",
        ["cs-CZ"]  = "czech",
        ["da-DK"]  = "danish",
        ["nl-NL"]  = "dutch",
        ["en-US"]  = "english",
        ["fi-FI"]  = "finnish",
        ["fr-FR"]  = "french",
        ["de-DE"]  = "german",
        ["el-GR"]  = "greek",
        ["hu-HU"]  = "hungarian",
        ["id-ID"]  = "indonesian",
        ["it-IT"]  = "italian",
        ["ja-JP"]  = "japanese",
        ["ko-KR"]  = "koreana",
        ["es-419"] = "latam",
        ["nb-NO"]  = "norwegian",
        ["pl-PL"]  = "polish",
        ["pt-PT"]  = "portuguese",
        ["ro-RO"]  = "romanian",
        ["ru-RU"]  = "russian",
        ["zh-CN"]  = "schinese",
        ["es-ES"]  = "spanish",
        ["sv-SE"]  = "swedish",
        ["zh-TW"]  = "tchinese",
        ["th-TH"]  = "thai",
        ["tr-TR"]  = "turkish",
        ["uk-UA"]  = "ukrainian",
        ["vi-VN"]  = "vietnamese",
    };

    private static readonly Regex LocaleTokenRegex = LocaleKeyRegex();

    private readonly ISharedSystem                        _shared;
    private readonly ILogger<KnifeCopier>                 _logger;
    private readonly IEconItemManager                     _econItemManager;
    private readonly IClientManager.DelegateClientCommand _commandCallback;

    private readonly Dictionary<string, Dictionary<string, string>> _localeTokens = new (StringComparer.OrdinalIgnoreCase);

    private IModSharpModuleInterface<IMenuManager>?      _menuManager;
    private IModSharpModuleInterface<ILocalizerManager>? _localizer;
    private bool                                         _localeFileLoaded;

    private static nint _giveNamedItemFn;

    public KnifeCopier(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        _shared          = sharedSystem;
        _logger          = sharedSystem.GetLoggerFactory().CreateLogger<KnifeCopier>();
        _econItemManager = sharedSystem.GetEconItemManager();
        _commandCallback = OnKnifeCopyCommand;
    }

    public bool Init()
    {
        _shared.GetClientManager().InstallCommandCallback(CommandName, _commandCallback);

        if (!_shared.GetModSharp().GetGameData().GetAddress(GiveNamedItemGameDataKey, out _giveNamedItemFn))
        {
            _logger.LogError("Failed to resolve '{Key}' from gamedata; cannot give knives.", GiveNamedItemGameDataKey);
        }

        GetLocaleTokens(DefaultLanguage);

        return true;
    }

    public void PostInit()
        => ResolveModules();

    public void OnLibraryConnected(string name)
        => ResolveModules();

    public void OnAllModulesLoaded()
        => ResolveModules(true);

    public void Shutdown()
        => _shared.GetClientManager().RemoveCommandCallback(CommandName, _commandCallback);

    public string DisplayName   => "KnifeCopier";
    public string DisplayAuthor => "Nukoooo";

    private readonly record struct KnifeEntry(CStrikeTeam Team, LoadoutSlot Slot, ushort DefIndex, int PaintKit, bool StatTrak);

    private ECommandAction OnKnifeCopyCommand(IGameClient client, StringCommand command)
    {
        if (_menuManager?.Instance is not { } menuManager)
        {
            client.Print(HudPrintChannel.Chat,
                         Prefix(L(client, "Chat.MenuUnavailable", "Menu system unavailable. Is '{0}' installed?",
                                  MenuManagerAssemblyName)));

            return ECommandAction.Handled;
        }

        if (client.GetPlayerController()?.GetPlayerPawn() is not { IsAlive: true })
        {
            client.Print(HudPrintChannel.Chat,
                         Prefix(L(client, "Chat.MustBeAliveCopy", "You must be alive to copy a knife skin.")));

            return ECommandAction.Handled;
        }

        menuManager.DisplayMenu(client, BuildPlayerMenu(client));

        return ECommandAction.Handled;
    }

    private Menu BuildPlayerMenu(IGameClient issuer)
    {
        var menu = new Menu();
        menu.SetTitle(L(issuer, "Menu.Title", "Copy knife skin from..."));

        var tokens        = GetIssuerTokens(issuer);
        var issuerSteamId = issuer.SteamId;
        var hasTargets    = false;

        foreach (var target in _shared.GetClientManager().GetGameClients(inGame: true))
        {
            if (target.IsFakeClient || target.IsHltv || !DebugMode && target.SteamId == issuerSteamId)
            {
                continue;
            }

            if (target.GetPlayerController() is not { } controller)
            {
                continue;
            }

            var knives = GetCopyableKnives(controller);

            if (knives.Count == 0)
            {
                continue;
            }

            hasTargets = true;

            var showTeam      = knives.Count > 1;
            var targetSteamId = target.SteamId;

            foreach (var knife in knives)
            {
                var entry   = knife;
                var isKnife = entry.Slot == LoadoutSlot.Melee;

                var skinText
                    = $"<font color='{KnifeNameColor}'>{DescribeKnife(entry.DefIndex, entry.PaintKit, entry.StatTrak, isKnife, tokens)}</font>";

                var label = showTeam
                    ? $"{target.Name} [{TeamTag(entry.Team)}] - {skinText}"
                    : $"{target.Name} - {skinText}";

                menu.AddItem(label, controller2 =>
                {
                    CopyKnifeSkin(controller2.Client, targetSteamId, entry.Team, entry.Slot);
                    controller2.Exit();
                });
            }
        }

        if (!hasTargets)
        {
            menu.AddDisabledItem(L(issuer, "Menu.NoPlayers", "No players with a custom knife"));
        }

        return menu;
    }

    private static List<KnifeEntry> GetCopyableKnives(IPlayerController controller)
    {
        var entries = new List<KnifeEntry>();
        var seen    = new HashSet<(ushort, int)>();

        foreach (var team in PlayableTeams)
        {
            AddSlotEntry(controller, team, LoadoutSlot.Melee, entries, seen);

            if (!DebugMode)
            {
                continue;
            }

            foreach (var slot in DebugSlots)
            {
                AddSlotEntry(controller, team, slot, entries, seen);
            }
        }

        return entries;
    }

    private static void AddSlotEntry(IPlayerController      controller,
                                     CStrikeTeam            team,
                                     LoadoutSlot            slot,
                                     List<KnifeEntry>       entries,
                                     HashSet<(ushort, int)> seen)
    {
        var view = controller.GetItemInLoadoutFromInventory(team, (int) slot);

        if (view is null || !view.Initialized)
        {
            return;
        }

        var defIndex = view.ItemDefinitionIndex;
        var paintKit = ReadCustomPaintKit(view);

        var interesting = slot == LoadoutSlot.Melee ? !IsDefaultKnife(defIndex) : paintKit != 0;

        if (interesting && seen.Add((defIndex, paintKit)))
        {
            entries.Add(new KnifeEntry(team, slot, defIndex, paintKit, view.Quality == 9));
        }
    }

    private void CopyKnifeSkin(IGameClient issuer, SteamID targetSteamId, CStrikeTeam team, LoadoutSlot slot)
    {
        if (_shared.GetClientManager().GetGameClient(targetSteamId)?.GetPlayerController() is not { } targetController)
        {
            issuer.Print(HudPrintChannel.Chat,
                         Prefix(L(issuer, "Chat.TargetUnavailable", "That player is no longer available.")));

            return;
        }

        if (issuer.GetPlayerController()?.GetPlayerPawn() is not { IsAlive: true })
        {
            issuer.Print(HudPrintChannel.Chat,
                         Prefix(L(issuer, "Chat.MustBeAliveReceive", "You must be alive to receive a knife skin.")));

            return;
        }

        var view = targetController.GetItemInLoadoutFromInventory(team, (int) slot);

        if (view is null || !view.Initialized)
        {
            issuer.Print(HudPrintChannel.Chat, Prefix(L(issuer, "Chat.CouldNotRead", "Could not read that player's knife.")));

            return;
        }

        var defIndex = view.ItemDefinitionIndex;
        var isKnife  = slot == LoadoutSlot.Melee;

        if (isKnife && IsDefaultKnife(defIndex))
        {
            issuer.Print(HudPrintChannel.Chat,
                         Prefix(L(issuer, "Chat.NoCustomKnife", "That player no longer has a custom knife on {0}.",
                                  TeamTag(team))));

            return;
        }

        if (_econItemManager.GetEconItemDefinitionByIndex(defIndex) is not { } def)
        {
            issuer.Print(HudPrintChannel.Chat, Prefix(L(issuer, "Chat.UnknownDef", "Unknown knife definition.")));

            return;
        }

        var classname   = def.DefinitionName;
        var paintKit    = ReadCustomPaintKit(view);
        var gearSlot    = ToGearSlot(slot);
        var description = DescribeKnife(defIndex, paintKit, view.Quality == 9, isKnife, GetIssuerTokens(issuer));

        _shared.GetModSharp().InvokeFrameAction(() =>
        {
            if (issuer.GetPlayerController()?.GetPlayerPawn() is not { IsAlive: true } pawn)
            {
                return;
            }

            if (_shared.GetClientManager().GetGameClient(targetSteamId)
                       ?.GetPlayerController()
                       ?.GetItemInLoadoutFromInventory(team, (int) slot) is not { Initialized: true } freshView)
            {
                return;
            }

            if (pawn.GetItemService() is not { } itemService)
            {
                return;
            }

            if (pawn.GetWeaponBySlot(gearSlot) is { } oldWeapon)
            {
                pawn.RemovePlayerItem(oldWeapon);
            }

            GiveNamedItem(itemService.GetAbsPtr(), classname, freshView.GetAbsPtr());

            issuer.Print(HudPrintChannel.Chat, Prefix(L(issuer, "Chat.Copied", "Copied {0}.", description)));
        });
    }

    private static GearSlot ToGearSlot(LoadoutSlot slot)
        => slot == LoadoutSlot.Melee ? GearSlot.Knife : GearSlot.Pistol;

    private static unsafe int ReadCustomPaintKit(IEconItemView view)
    {
        var self   = view.GetAbsPtr();
        var vtable = *(nint**) self;
        var fn     = (delegate* unmanaged<nint, int>) vtable[VTableGetCustomPaintKitIndex];

        return fn(self);
    }

    private static unsafe void GiveNamedItem(nint itemService, string classname, nint view)
    {
        if (_giveNamedItemFn == nint.Zero)
        {
            return;
        }

        var count = Encoding.UTF8.GetByteCount(classname);

        var name = stackalloc byte[count + 1];
        Encoding.UTF8.GetBytes(classname, new Span<byte>(name, count));
        name[count] = 0;

        ((delegate* unmanaged<nint, byte*, int, nint, byte, nint, nint>) _giveNamedItemFn)(itemService, name, 0, view, 1,
            nint.Zero);
    }

    private static bool IsDefaultKnife(ushort defIndex)
        => defIndex is DefaultKnifeCt or DefaultKnifeTe;

    private static string TeamTag(CStrikeTeam team)
        => team == CStrikeTeam.CT ? "CT" : "T";

    private static string Prefix(string message)
        => $"[KnifeCopier] {message}";

    private string L(IGameClient client, string key, string english, params object?[] args)
    {
        if (_localizer?.Instance is { } lm && lm.For(client).TryText(key, out var text, args))
        {
            return text;
        }

        return args.Length == 0 ? english : string.Format(english, args);
    }

    private string DescribeKnife(ushort defIndex, int paintKit, bool statTrak, bool isKnife, Dictionary<string, string> tokens)
    {
        var def = _econItemManager.GetEconItemDefinitionByIndex(defIndex);

        var model = ResolveToken(def?.ItemBaseName, tokens)
                    ?? (Enum.GetName((EconItemId) defIndex) is { } name
                        ? name.Replace("Knife", string.Empty).Trim()
                        : $"Weapon {defIndex}");

        var star   = isKnife ? "★ " : string.Empty;
        var prefix = statTrak ? $"{ResolveToken("strange", tokens) ?? "StatTrak™"} " : string.Empty;

        return paintKit == 0
            ? $"{star}{prefix}{model}"
            : $"{star}{prefix}{model} | {ResolvePaintKitName(paintKit, tokens)}";
    }

    private string ResolvePaintKitName(int paintKit, Dictionary<string, string> tokens)
    {
        if (_econItemManager.GetPaintKits().TryGetValue((uint) paintKit, out var pk))
        {
            if (ResolveToken(pk.DescriptionTag, tokens) is { } localized)
            {
                return localized;
            }

            if (!string.IsNullOrEmpty(pk.Name))
            {
                return pk.Name;
            }
        }

        return $"#{paintKit}";
    }

    private static string? ResolveToken(string? token, Dictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var key = token[0] == '#' ? token[1..] : token;

        return tokens.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    private Dictionary<string, string> GetIssuerTokens(IGameClient issuer)
    {
        var culture   = _localizer?.Instance?.For(issuer).Culture.Name ?? "en-US";
        var steamLang = CultureToSteamLang.GetValueOrDefault(culture, DefaultLanguage);

        return GetLocaleTokens(steamLang);
    }

    private Dictionary<string, string> GetLocaleTokens(string steamLang)
    {
        if (_localeTokens.TryGetValue(steamLang, out var cached))
        {
            return cached;
        }

        var dict = ParseLocaleFile($"resource/csgo_{steamLang}.txt");

        if (dict is null && !steamLang.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Locale file for '{Lang}' unavailable; falling back to english.", steamLang);
            dict = GetLocaleTokens(DefaultLanguage);
        }

        dict ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        _localeTokens[steamLang] = dict;

        return dict;
    }

    private Dictionary<string, string>? ParseLocaleFile(string path)
    {
        using var file = _shared.GetFileManager().OpenFile(path, "GAME");

        if (file is null)
        {
            return null;
        }

        var size = file.Size();

        if (size <= 0)
        {
            return null;
        }

        var buffer = new byte[size];
        file.Read(buffer);

        var text = Encoding.UTF8.GetString(buffer);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in LocaleTokenRegex.Matches(text))
        {
            dict[match.Groups[1].Value] = match.Groups[2].Value;
        }

        _logger.LogInformation("Loaded {Count} item-name tokens from '{File}'.", dict.Count, path);

        return dict;
    }

    private void ResolveModules(bool logFailure = false)
    {
        if (_menuManager?.Instance is null)
        {
            _menuManager = _shared.GetSharpModuleManager().GetOptionalSharpModuleInterface<IMenuManager>(IMenuManager.Identity);
        }

        if (_localizer?.Instance is null)
        {
            _localizer = _shared.GetSharpModuleManager()
                                .GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity);
        }

        if (!_localeFileLoaded && _localizer?.Instance is { } lm)
        {
            lm.LoadLocaleFile(LocaleFileName, true);
            _localeFileLoaded = true;
        }

        if (!logFailure)
        {
            return;
        }

        if (_menuManager?.Instance is null)
        {
            _logger.LogWarning("MenuManager not found. Install '{Assembly}' to use the knife picker.", MenuManagerAssemblyName);
        }

        if (_localizer?.Instance is null)
        {
            _logger.LogWarning("LocalizerManager not found; KnifeCopier strings will be English only.");
        }
    }

    [GeneratedRegex("\"((?:PaintKit_|SFUI_WPNHUD_)[^\"]*|strange)\"\\s+\"([^\"]*)\"",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex LocaleKeyRegex();
}
