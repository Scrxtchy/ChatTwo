using System.Diagnostics;
using System.Numerics;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using ImGuiScene;
using Lumina.Excel.GeneratedSheets;

namespace ChatTwo.Ui;

internal sealed class ChatLog : IUiComponent {
    private const string ChatChannelPicker = "chat-channel-picker";

    internal PluginUi Ui { get; }

    internal bool Activate;
    internal string Chat = string.Empty;
    private readonly TextureWrap? _fontIcon;
    private readonly List<string> _inputBacklog = new();
    private int _inputBacklogIdx = -1;
    internal int LastTab { get; private set; }
    private InputChannel? _tempChannel;
    private TellTarget? _tellTarget;
    private readonly Stopwatch _lastResize = new();
    private CommandHelp? _commandHelp;

    internal Vector2 LastWindowSize { get; private set; } = Vector2.Zero;
    internal Vector2 LastWindowPos { get; private set; } = Vector2.Zero;

    private PayloadHandler PayloadHandler { get; }
    private Lender<PayloadHandler> HandlerLender { get; }
    private Dictionary<string, ChatType> TextCommandChannels { get; } = new();
    private HashSet<string> AllCommands { get; } = new();

    internal ChatLog(PluginUi ui) {
        this.Ui = ui;
        this.PayloadHandler = new PayloadHandler(this.Ui, this);
        this.HandlerLender = new Lender<PayloadHandler>(() => new PayloadHandler(this.Ui, this));

        this.SetUpTextCommandChannels();
        this.SetUpAllCommands();

        this.Ui.Plugin.Commands.Register("/clearlog2", "Clear the Chat 2 chat log").Execute += this.ClearLog;
        this.Ui.Plugin.Commands.Register("/chat2").Execute += this.ToggleChat;

        this._fontIcon = this.Ui.Plugin.DataManager.GetImGuiTexture("common/font/fonticon_ps5.tex");

        this.Ui.Plugin.Functions.Chat.Activated += this.Activated;
        this.Ui.Plugin.ClientState.Login += this.Login;
        this.Ui.Plugin.ClientState.Logout += this.Logout;
    }

    public void Dispose() {
        this.Ui.Plugin.ClientState.Logout -= this.Logout;
        this.Ui.Plugin.ClientState.Login -= this.Login;
        this.Ui.Plugin.Functions.Chat.Activated -= this.Activated;
        this._fontIcon?.Dispose();
        this.Ui.Plugin.Commands.Register("/chat2").Execute -= this.ToggleChat;
        this.Ui.Plugin.Commands.Register("/clearlog2").Execute -= this.ClearLog;
    }

    private void Logout(object? sender, EventArgs e) {
        foreach (var tab in this.Ui.Plugin.Config.Tabs) {
            tab.Clear();
        }
    }

    private void Login(object? sender, EventArgs e) {
        this.Ui.Plugin.Store.FilterAllTabs(false);
    }

    private void Activated(ChatActivatedArgs args) {
        this.Activate = true;
        if (args.AddIfNotPresent != null && !this.Chat.Contains(args.AddIfNotPresent)) {
            this.Chat += args.AddIfNotPresent;
        }

        if (args.Input != null) {
            this.Chat += args.Input;
        }

        var (info, reason, target) = (args.ChannelSwitchInfo, args.TellReason, args.TellTarget);

        if (info.Channel != null) {
            var prevTemp = this._tempChannel;

            if (info.Permanent) {
                this.Ui.Plugin.Functions.Chat.SetChannel(info.Channel.Value);
            } else {
                this._tempChannel = info.Channel.Value;
            }

            if (info.Channel is InputChannel.Tell) {
                if (info.Rotate != RotateMode.None) {
                    var idx = prevTemp != InputChannel.Tell
                        ? 0
                        : info.Rotate == RotateMode.Reverse
                            ? -1
                            : 1;

                    var tellInfo = this.Ui.Plugin.Functions.Chat.GetTellHistoryInfo(idx);
                    if (tellInfo != null && reason != null) {
                        this._tellTarget = new TellTarget(tellInfo.Name, (ushort) tellInfo.World, tellInfo.ContentId, reason.Value);
                    }
                } else {
                    this._tellTarget = null;

                    if (target != null) {
                        this._tellTarget = target;
                    }
                }
            } else {
                this._tellTarget = null;
            }

            var mode = prevTemp == null
                ? RotateMode.None
                : info.Rotate;

            if (info.Channel is InputChannel.Linkshell1 && info.Rotate != RotateMode.None) {
                var idx = this.Ui.Plugin.Functions.Chat.RotateLinkshellHistory(mode);
                this._tempChannel = info.Channel.Value + (uint) idx;
            } else if (info.Channel is InputChannel.CrossLinkshell1 && info.Rotate != RotateMode.None) {
                var idx = this.Ui.Plugin.Functions.Chat.RotateCrossLinkshellHistory(mode);
                this._tempChannel = info.Channel.Value + (uint) idx;
            }
        }

        if (info.Text != null && this.Chat.Length == 0) {
            this.Chat = info.Text;
        }
    }

    private bool IsValidCommand(string command) {
        return this.Ui.Plugin.CommandManager.Commands.ContainsKey(command)
               || this.AllCommands.Contains(command);
    }

    private void ClearLog(string command, string arguments) {
        switch (arguments) {
            case "all":
                foreach (var tab in this.Ui.Plugin.Config.Tabs) {
                    tab.Clear();
                }

                break;
            case "help":
                this.Ui.Plugin.ChatGui.Print("- /clearlog2: clears the active tab's log");
                this.Ui.Plugin.ChatGui.Print("- /clearlog2 all: clears all tabs' logs and the global history");
                this.Ui.Plugin.ChatGui.Print("- /clearlog2 help: shows this help");

                break;
            default:
                if (this.LastTab > -1 && this.LastTab < this.Ui.Plugin.Config.Tabs.Count) {
                    this.Ui.Plugin.Config.Tabs[this.LastTab].Clear();
                }

                break;
        }
    }

    private void ToggleChat(string command, string arguments) {
        var parts = arguments.Split(' ');
        if (parts.Length < 2 || parts[0] != "chat") {
            return;
        }

        switch (parts[1]) {
            case "hide":
                this._hideState = HideState.User;
                break;
            case "show":
                this._hideState = HideState.None;
                break;
            case "toggle":
                this._hideState = this._hideState switch {
                    HideState.User or HideState.CutsceneOverride => HideState.None,
                    HideState.Cutscene => HideState.CutsceneOverride,
                    HideState.None => HideState.User,
                    _ => this._hideState,
                };

                break;
        }
    }

    private void SetUpTextCommandChannels() {
        this.TextCommandChannels.Clear();

        foreach (var input in Enum.GetValues<InputChannel>()) {
            var commands = input.TextCommands(this.Ui.Plugin.DataManager);
            if (commands == null) {
                continue;
            }

            var type = input.ToChatType();
            foreach (var command in commands) {
                this.AddTextCommandChannel(command, type);
            }
        }

        var echo = this.Ui.Plugin.DataManager.GetExcelSheet<TextCommand>()?.GetRow(116);
        if (echo != null) {
            this.AddTextCommandChannel(echo, ChatType.Echo);
        }
    }

    private void AddTextCommandChannel(TextCommand command, ChatType type) {
        this.TextCommandChannels[command.Command] = type;
        this.TextCommandChannels[command.ShortCommand] = type;
        this.TextCommandChannels[command.Alias] = type;
        this.TextCommandChannels[command.ShortAlias] = type;
    }

    private void SetUpAllCommands() {
        if (this.Ui.Plugin.DataManager.GetExcelSheet<TextCommand>() is not { } commands) {
            return;
        }

        var commandNames = commands.SelectMany(cmd => new[] {
            cmd.Command.RawString,
            cmd.ShortCommand.RawString,
            cmd.Alias.RawString,
            cmd.ShortAlias.RawString,
        });

        foreach (var command in commandNames) {
            this.AllCommands.Add(command);
        }
    }

    private void AddBacklog(string message) {
        for (var i = 0; i < this._inputBacklog.Count; i++) {
            if (this._inputBacklog[i] != message) {
                continue;
            }

            this._inputBacklog.RemoveAt(i);
            break;
        }

        this._inputBacklog.Add(message);
    }

    private static float GetRemainingHeightForMessageLog() {
        var lineHeight = 0;
        return ImGui.GetContentRegionAvail().Y
               - lineHeight * 2
               - ImGui.GetStyle().ItemSpacing.Y
               - ImGui.GetStyle().FramePadding.Y * 2;
    }

    private unsafe ImGuiViewport* _lastViewport;
    private bool _wasDocked;

   /* private void HandleKeybinds(bool modifiersOnly = false) {
        var modifierState = (ModifierFlag) 0;
        if (ImGui.GetIO().KeyAlt) {
            modifierState |= ModifierFlag.Alt;
        }

        if (ImGui.GetIO().KeyCtrl) {
            modifierState |= ModifierFlag.Ctrl;
        }

        if (ImGui.GetIO().KeyShift) {
            modifierState |= ModifierFlag.Shift;
        }

        var turnedOff = new Dictionary<VirtualKey, (uint, string)>();
        foreach (var (toIntercept, keybind) in this.Ui.Plugin.Functions.Chat.Keybinds) {
            if (toIntercept is "CMD_CHAT" or "CMD_COMMAND") {
                continue;
            }

            void Intercept(VirtualKey key, ModifierFlag modifier) {
                var modifierPressed = this.Ui.Plugin.Config.KeybindMode switch {
                    KeybindMode.Strict => modifier == modifierState,
                    KeybindMode.Flexible => modifierState.HasFlag(modifier),
                    _ => false,
                };
                if (!ImGui.IsKeyPressed((int) key) || !modifierPressed || modifier == 0 && modifiersOnly) {
                    return;
                }

                var bits = BitOperations.PopCount((uint) modifier);
                if (!turnedOff.TryGetValue(key, out var previousBits) || previousBits.Item1 < bits) {
                    turnedOff[key] = ((uint) bits, toIntercept);
                }
            }

            Intercept(keybind.Key1, keybind.Modifier1);
            Intercept(keybind.Key2, keybind.Modifier2);
        }

        foreach (var (_, (_, keybind)) in turnedOff) {
            if (!GameFunctions.Chat.KeybindsToIntercept.TryGetValue(keybind, out var info)) {
                continue;
            }

            try {
                TellReason? reason = info.Channel == InputChannel.Tell ? TellReason.Reply : null;
                this.Activated(new ChatActivatedArgs(info) {
                    TellReason = reason,
                });
            } catch (Exception ex) {
                PluginLog.LogError(ex, "Error in chat Activated event");
            }
        }
    }*/

    private bool CutsceneActive {
        get {
            var condition = this.Ui.Plugin.Condition;
            return condition[ConditionFlag.OccupiedInCutSceneEvent]
                   || condition[ConditionFlag.WatchingCutscene78];
        }
    }

    private bool GposeActive {
        get {
            var condition = this.Ui.Plugin.Condition;
            return condition[ConditionFlag.WatchingCutscene];
        }
    }

    private enum HideState {
        None,
        Cutscene,
        CutsceneOverride,
        User,
    }

    private HideState _hideState = HideState.None;

    public void Draw() {
        if (!this.DrawChatLog()) {
            return;
        }

        this._commandHelp?.Draw();
        this.DrawPopOuts();
    }

    /// <returns>true if window was rendered</returns>
    private unsafe bool DrawChatLog() {
        // if the chat has no hide state and in a cutscene, set the hide state to cutscene
        if (this.Ui.Plugin.Config.HideDuringCutscenes && this._hideState == HideState.None && (this.CutsceneActive || this.GposeActive)) {
            this._hideState = HideState.Cutscene;
        }

        // if the chat is hidden because of a cutscene and no longer in a cutscene, set the hide state to none
        if ((this._hideState is HideState.Cutscene or HideState.CutsceneOverride) && !this.CutsceneActive && !this.GposeActive) {
            this._hideState = HideState.None;
        }

        // if the chat is hidden because of a cutscene and the chat has been activated, show chat
        if (this._hideState == HideState.Cutscene && this.Activate) {
            this._hideState = HideState.CutsceneOverride;
            
        }

        // if the user hid the chat and is now activating chat, reset the hide state
        if (this._hideState == HideState.User && this.Activate) {
            this._hideState = HideState.None;
        }

        if (this.Activate) this.Activate = false;

        /*if (this._hideState is HideState.Cutscene or HideState.User) {
            return false;
        }

        if (this.Ui.Plugin.Config.HideWhenNotLoggedIn && !this.Ui.Plugin.ClientState.IsLoggedIn) {
            return false;
        }*/
        var addon = GetChatLogAddon();

        if (addon is null || !addon->AtkUnitBase.IsVisible)
            return false;

        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!this.Ui.Plugin.Config.CanMove) {
            flags |= ImGuiWindowFlags.NoMove;
        }

        if (!this.Ui.Plugin.Config.CanResize) {
            flags |= ImGuiWindowFlags.NoResize;
        }

        if (!this.Ui.Plugin.Config.ShowTitleBar) {
            flags |= ImGuiWindowFlags.NoTitleBar;
        }

        if (this._lastViewport == ImGuiHelpers.MainViewport.NativePtr && !this._wasDocked) {
            ImGui.SetNextWindowBgAlpha(this.Ui.Plugin.Config.WindowAlpha / 100f);
        }

        var textinput = addon->AtkUnitBase.UldManager.NodeList[15]->GetAsAtkComponentNode();
        AtkResNode* at = textinput->Component->UldManager.NodeList[3];

        if (CollisionAdjust(GetGenericAddon("ContextMenu"))) return false;
        //TODO: Clickthrough flags not operating

        string[] coladdons = { "ItemDetail", "ActionDetail" };

        bool hideWindowCollide = (at->IsVisible || coladdons.Any(addon => CollisionAdjust(GetGenericAddon(addon), 3)));

        if (hideWindowCollide)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.15f);
            ImGui.SetNextWindowBgAlpha(0.15f);
        }

        ImGui.SetNextWindowSize(new Vector2(500, 250) * ImGuiHelpers.GlobalScale, ImGuiCond.FirstUseEver);

        if (!ImGui.Begin($"{this.Ui.Plugin.Name}###chat2", flags)) {
            this._lastViewport = ImGui.GetWindowViewport().NativePtr;
            this._wasDocked = ImGui.IsWindowDocked();
            ImGui.End();
            return false;
        }

        var resized = this.LastWindowSize != ImGui.GetWindowSize();
        this.LastWindowSize = ImGui.GetWindowSize();
        this.LastWindowPos = ImGui.GetWindowPos();

        if (resized) {
            this._lastResize.Restart();
        }

        this._lastViewport = ImGui.GetWindowViewport().NativePtr;
        this._wasDocked = ImGui.IsWindowDocked();

        var currentTab = this.Ui.Plugin.Config.SidebarTabView
            ? this.DrawTabSidebar()
            : this.DrawTabBar();

        /*if (this.Activate) {
            this._hideState = HideState.None;
        }*/

        Tab? activeTab = null;
        if (currentTab > -1 && currentTab < this.Ui.Plugin.Config.Tabs.Count) {
            activeTab = this.Ui.Plugin.Config.Tabs[currentTab];
        }

        /*ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        try {
            if (this._tellTarget != null) {
                var world = this.Ui.Plugin.DataManager.GetExcelSheet<World>()
                    ?.GetRow(this._tellTarget.World)
                    ?.Name
                    ?.RawString ?? "???";

                this.DrawChunks(new Chunk[] {
                    new TextChunk(ChunkSource.None, null, "Tell "),
                    new TextChunk(ChunkSource.None, null, this._tellTarget.Name),
                    new IconChunk(ChunkSource.None, null, BitmapFontIcon.CrossWorld),
                    new TextChunk(ChunkSource.None, null, world),
                });
            } else if (this._tempChannel != null) {
                if (this._tempChannel.Value.IsLinkshell()) {
                    var idx = (uint) this._tempChannel.Value - (uint) InputChannel.Linkshell1;
                    var lsName = this.Ui.Plugin.Functions.Chat.GetLinkshellName(idx);
                    ImGui.TextUnformatted($"LS #{idx + 1}: {lsName}");
                } else if (this._tempChannel.Value.IsCrossLinkshell()) {
                    var idx = (uint) this._tempChannel.Value - (uint) InputChannel.CrossLinkshell1;
                    var cwlsName = this.Ui.Plugin.Functions.Chat.GetCrossLinkshellName(idx);
                    ImGui.TextUnformatted($"CWLS [{idx + 1}]: {cwlsName}");
                } else {
                    ImGui.TextUnformatted(this._tempChannel.Value.ToChatType().Name());
                }
            } else if (activeTab is { Channel: { } channel }) {
                ImGui.TextUnformatted(channel.ToChatType().Name());
            } else {
                this.DrawChunks(this.Ui.Plugin.Functions.Chat.Channel.name);
            }
        } finally {
            ImGui.PopStyleVar();
        }

        var beforeIcon = ImGui.GetCursorPos();

        if (ImGuiUtil.IconButton(FontAwesomeIcon.Comment) && activeTab is not { Channel: { } }) {
            ImGui.OpenPopup(ChatChannelPicker);
        }

        if (activeTab is { Channel: { } } && ImGui.IsItemHovered()) {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Language.ChatLog_SwitcherDisabled);
            ImGui.EndTooltip();
        }

        if (ImGui.BeginPopup(ChatChannelPicker)) {
            foreach (var channel in Enum.GetValues<InputChannel>()) {
                var name = this.Ui.Plugin.DataManager.GetExcelSheet<LogFilter>()!
                    .FirstOrDefault(row => row.LogKind == (byte) channel.ToChatType())
                    ?.Name
                    ?.RawString ?? channel.ToString();

                if (ImGui.Selectable(name)) {
                    this.Ui.Plugin.Functions.Chat.SetChannel(channel);
                    this._tellTarget = null;
                }
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine();
        var afterIcon = ImGui.GetCursorPos();

        var buttonWidth = afterIcon.X - beforeIcon.X;
        var showNovice = this.Ui.Plugin.Config.ShowNoviceNetwork && this.Ui.Plugin.Functions.IsMentor();
        var inputWidth = ImGui.GetContentRegionAvail().X - buttonWidth * (showNovice ? 2 : 1);

        var inputType = this._tempChannel?.ToChatType() ?? activeTab?.Channel?.ToChatType() ?? this.Ui.Plugin.Functions.Chat.Channel.channel.ToChatType();
        if (this.Chat.Trim().StartsWith('/')) {
            var command = this.Chat.Split(' ')[0];
            if (this.TextCommandChannels.TryGetValue(command, out var channel)) {
                inputType = channel;
            }

            if (!this.IsValidCommand(command)) {
                inputType = ChatType.Error;
            }
        }

        var normalColour = *ImGui.GetStyleColorVec4(ImGuiCol.Text);

        var inputColour = this.Ui.Plugin.Config.ChatColours.TryGetValue(inputType, out var inputCol)
            ? inputCol
            : inputType.DefaultColour();

        if (inputColour != null) {
            ImGui.PushStyleColor(ImGuiCol.Text, ColourUtil.RgbaToAbgr(inputColour.Value));
        }

        ImGui.SetNextItemWidth(inputWidth);
        const ImGuiInputTextFlags inputFlags = ImGuiInputTextFlags.EnterReturnsTrue
                                               | ImGuiInputTextFlags.CallbackAlways
                                               | ImGuiInputTextFlags.CallbackCharFilter
                                               | ImGuiInputTextFlags.CallbackHistory;
        if (ImGui.InputText("##chat2-input", ref this.Chat, 500, inputFlags, this.Callback)) {
            if (!string.IsNullOrWhiteSpace(this.Chat)) {
                var trimmed = this.Chat.Trim();
                this.AddBacklog(trimmed);
                this._inputBacklogIdx = -1;

                if (!trimmed.StartsWith('/')) {
                    if (this._tellTarget != null) {
                        var target = this._tellTarget;
                        var reason = target.Reason;
                        var world = this.Ui.Plugin.DataManager.GetExcelSheet<World>()?.GetRow(target.World);
                        if (world is { IsPublic: true }) {
                            if (reason == TellReason.Reply && this.Ui.Plugin.Common.Functions.FriendList.List.Any(friend => friend.ContentId == target.ContentId)) {
                                reason = TellReason.Friend;
                            }

                            this.Ui.Plugin.Functions.Chat.SendTell(reason, target.ContentId, target.Name, (ushort) world.RowId, trimmed);
                        }

                        if (this._tempChannel is InputChannel.Tell) {
                            this._tellTarget = null;
                        }

                        goto Skip;
                    }


                    if (this._tempChannel != null) {
                        trimmed = $"{this._tempChannel.Value.Prefix()} {trimmed}";
                    } else if (activeTab is { Channel: { } channel }) {
                        trimmed = $"{channel.Prefix()} {trimmed}";
                    }
                }

                this.Ui.Plugin.Common.Functions.Chat.SendMessageUnsafe(Encoding.UTF8.GetBytes(trimmed));
            }

            Skip:
            this.Chat = string.Empty;
        }

        if (ImGui.IsItemActive()) {
            this.HandleKeybinds(true);
        }

        if (!this.Activate && !ImGui.IsItemActive()) {
            if (this._tempChannel is InputChannel.Tell) {
                this._tellTarget = null;
            }

            this._tempChannel = null;
        }

        if (ImGui.BeginPopupContextItem()) {
            ImGui.PushStyleColor(ImGuiCol.Text, normalColour);

            try {
                if (ImGui.Selectable(Language.ChatLog_HideChat)) {
                    this.UserHide();
                }
            } finally {
                ImGui.PopStyleColor();
            }

            ImGui.EndPopup();
        }

        if (inputColour != null) {
            ImGui.PopStyleColor();
        }

        ImGui.SameLine();

        if (ImGuiUtil.IconButton(FontAwesomeIcon.Cog)) {
            this.Ui.SettingsVisible ^= true;
        }

        if (showNovice) {
            ImGui.SameLine();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.Leaf)) {
                this.Ui.Plugin.Functions.ClickNoviceNetworkButton();
            }
        }*/

        if (hideWindowCollide) ImGui.PopStyleVar();

        ImGui.End();

        return true;
    }

    internal void UserHide() {
        this._hideState = HideState.User;
        this.Activate = false;
    }

    private void DrawMessageLog(Tab tab, PayloadHandler handler, float childHeight, bool switchedTab) {
        if (ImGui.BeginChild("##chat2-messages", new Vector2(-1, childHeight))) {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            var table = tab.DisplayTimestamp && this.Ui.Plugin.Config.PrettierTimestamps;

            var oldCellPaddingY = ImGui.GetStyle().CellPadding.Y;
            if (this.Ui.Plugin.Config.PrettierTimestamps && this.Ui.Plugin.Config.MoreCompactPretty) {
                var padding = ImGui.GetStyle().CellPadding;
                padding.Y = 0;

                ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, padding);
            }

            if (table) {
                if (!ImGui.BeginTable("timestamp-table", 2, ImGuiTableFlags.PreciseWidths)) {
                    goto EndChild;
                }

                ImGui.TableSetupColumn("timestamps", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("messages", ImGuiTableColumnFlags.WidthStretch);
            }

            try {
                tab.MessagesMutex.Wait();

                var reset = false;
                if (this._lastResize.IsRunning && this._lastResize.Elapsed.TotalSeconds > 0.25) {
                    this._lastResize.Stop();
                    this._lastResize.Reset();
                    reset = true;
                }

                var lastPos = ImGui.GetCursorPosY();
                var lastTimestamp = string.Empty;
                foreach (var message in tab.Messages) {
                    if (reset) {
                        message.Height = null;
                        message.IsVisible = false;
                    }

                    // go to next row
                    if (table) {
                        ImGui.TableNextColumn();
                    }

                    // message has rendered once
                    if (message.Height.HasValue) {
                        // message isn't visible, so render dummy
                        if (!message.IsVisible) {
                            var beforeDummy = ImGui.GetCursorPos();

                            if (table) {
                                // skip to the message column for vis test
                                ImGui.TableNextColumn();
                            }

                            ImGui.Dummy(new Vector2(10f, message.Height.Value));
                            message.IsVisible = ImGui.IsItemVisible();

                            if (message.IsVisible) {
                                if (table) {
                                    ImGui.TableSetColumnIndex(0);
                                }

                                ImGui.SetCursorPos(beforeDummy);
                            } else {
                                goto UpdateMessage;
                            }
                        }
                    }

                    if (tab.DisplayTimestamp) {
                        var timestamp = message.Date.ToLocalTime().ToString("t");
                        if (table) {
                            if (!this.Ui.Plugin.Config.HideSameTimestamps || timestamp != lastTimestamp) {
                                ImGui.TextUnformatted(timestamp);
                                lastTimestamp = timestamp;
                            }
                        } else {
                            this.DrawChunk(new TextChunk(ChunkSource.None, null, $"[{timestamp}]") {
                                Foreground = 0xFFFFFFFF,
                            });
                            ImGui.SameLine();
                        }
                    }

                    if (table) {
                        ImGui.TableNextColumn();
                    }

                    var lineWidth = ImGui.GetContentRegionAvail().X;

                    var beforeDraw = ImGui.GetCursorScreenPos();
                    if (message.Sender.Count > 0) {
                        this.DrawChunks(message.Sender, true, handler, lineWidth);
                        ImGui.SameLine();
                    }

                    if (message.Content.Count == 0) {
                        this.DrawChunks(new[] { new TextChunk(ChunkSource.Content, null, " ") }, true, handler, lineWidth);
                    } else {
                        this.DrawChunks(message.Content, true, handler, lineWidth);
                    }

                    var afterDraw = ImGui.GetCursorScreenPos();

                    message.Height = ImGui.GetCursorPosY() - lastPos;
                    if (this.Ui.Plugin.Config.PrettierTimestamps && !this.Ui.Plugin.Config.MoreCompactPretty) {
                        message.Height -= oldCellPaddingY * 2;
                        beforeDraw.Y += oldCellPaddingY;
                        afterDraw.Y -= oldCellPaddingY;
                    }

                    message.IsVisible = ImGui.IsRectVisible(beforeDraw, afterDraw);

                    UpdateMessage:
                    lastPos = ImGui.GetCursorPosY();
                }
            } finally {
                tab.MessagesMutex.Release();
                ImGui.PopStyleVar(this.Ui.Plugin.Config.PrettierTimestamps && this.Ui.Plugin.Config.MoreCompactPretty ? 2 : 1);
            }

            if (switchedTab || ImGui.GetScrollY() >= ImGui.GetScrollMaxY()) {
                ImGui.SetScrollHereY(1f);
            }

            handler.Draw();

            if (table) {
                ImGui.EndTable();
            }
        }

        EndChild:
        ImGui.EndChild();
    }

    private int DrawTabBar() {
        var currentTab = -1;

        if (!ImGui.BeginTabBar("##chat2-tabs")) {
            return currentTab;
        }

        for (var tabI = 0; tabI < this.Ui.Plugin.Config.Tabs.Count; tabI++) {
            var tab = this.Ui.Plugin.Config.Tabs[tabI];
            if (tab.PopOut) {
                continue;
            }

            var unread = tabI == this.LastTab || tab.UnreadMode == UnreadMode.None || tab.Unread == 0 ? "" : $" ({tab.Unread})";
            var draw = ImGui.BeginTabItem($"{tab.Name}{unread}###log-tab-{tabI}");
            this.DrawTabContextMenu(tab, tabI);

            if (!draw) {
                continue;
            }

            currentTab = tabI;
            var switchedTab = this.LastTab != tabI;
            this.LastTab = tabI;
            tab.Unread = 0;

            this.DrawMessageLog(tab, this.PayloadHandler, GetRemainingHeightForMessageLog(), switchedTab);

            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();

        return currentTab;
    }

    private int DrawTabSidebar() {
        var currentTab = -1;

        if (!ImGui.BeginTable("tabs-table", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable)) {
            return -1;
        }

        ImGui.TableSetupColumn("tabs", ImGuiTableColumnFlags.None, 1);
        ImGui.TableSetupColumn("chat", ImGuiTableColumnFlags.None, 4);

        ImGui.TableNextColumn();

        var switchedTab = false;
        var childHeight = GetRemainingHeightForMessageLog();
        if (ImGui.BeginChild("##chat2-tab-sidebar", new Vector2(-1, childHeight))) {
            for (var tabI = 0; tabI < this.Ui.Plugin.Config.Tabs.Count; tabI++) {
                var tab = this.Ui.Plugin.Config.Tabs[tabI];
                if (tab.PopOut) {
                    continue;
                }

                var unread = tabI == this.LastTab || tab.UnreadMode == UnreadMode.None || tab.Unread == 0 ? "" : $" ({tab.Unread})";
                var clicked = ImGui.Selectable($"{tab.Name}{unread}###log-tab-{tabI}", this.LastTab == tabI);
                this.DrawTabContextMenu(tab, tabI);

                if (!clicked) {
                    continue;
                }

                currentTab = tabI;
                switchedTab = this.LastTab != tabI;
                this.LastTab = tabI;
            }
        }

        ImGui.EndChild();

        ImGui.TableNextColumn();

        if (currentTab == -1 && this.LastTab < this.Ui.Plugin.Config.Tabs.Count) {
            currentTab = this.LastTab;
            this.Ui.Plugin.Config.Tabs[currentTab].Unread = 0;
        }

        if (currentTab > -1) {
            this.DrawMessageLog(this.Ui.Plugin.Config.Tabs[currentTab], this.PayloadHandler, childHeight, switchedTab);
        }

        ImGui.EndTable();

        return currentTab;
    }

    private void DrawTabContextMenu(Tab tab, int i) {
        if (!ImGui.BeginPopupContextItem()) {
            return;
        }

        var tabs = this.Ui.Plugin.Config.Tabs;
        var anyChanged = false;

        ImGui.PushID($"tab-context-menu-{i}");

        ImGui.SetNextItemWidth(250f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##tab-name", ref tab.Name, 128)) {
            anyChanged = true;
        }

        if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, tooltip: Language.ChatLog_Tabs_Delete)) {
            tabs.RemoveAt(i);
            anyChanged = true;
        }

        ImGui.SameLine();

        var (leftIcon, leftTooltip) = this.Ui.Plugin.Config.SidebarTabView
            ? (FontAwesomeIcon.ArrowUp, Language.ChatLog_Tabs_MoveUp)
            : (FontAwesomeIcon.ArrowLeft, Language.ChatLog_Tabs_MoveLeft);
        if (ImGuiUtil.IconButton(leftIcon, tooltip: leftTooltip) && i > 0) {
            (tabs[i - 1], tabs[i]) = (tabs[i], tabs[i - 1]);
            ImGui.CloseCurrentPopup();
            anyChanged = true;
        }

        ImGui.SameLine();

        var (rightIcon, rightTooltip) = this.Ui.Plugin.Config.SidebarTabView
            ? (FontAwesomeIcon.ArrowDown, Language.ChatLog_Tabs_MoveDown)
            : (FontAwesomeIcon.ArrowRight, Language.ChatLog_Tabs_MoveRight);
        if (ImGuiUtil.IconButton(rightIcon, tooltip: rightTooltip) && i < tabs.Count - 1) {
            (tabs[i + 1], tabs[i]) = (tabs[i], tabs[i + 1]);
            ImGui.CloseCurrentPopup();
            anyChanged = true;
        }

        ImGui.SameLine();
        if (ImGuiUtil.IconButton(FontAwesomeIcon.WindowRestore, tooltip: Language.ChatLog_Tabs_PopOut)) {
            tab.PopOut = true;
            anyChanged = true;
        }

        if (anyChanged) {
            this.Ui.Plugin.SaveConfig();
        }

        ImGui.PopID();
        ImGui.EndPopup();
    }

    private readonly List<bool> _popOutDocked = new();

    private void DrawPopOuts() {
        this.HandlerLender.ResetCounter();

        if (this._popOutDocked.Count != this.Ui.Plugin.Config.Tabs.Count) {
            this._popOutDocked.Clear();
            this._popOutDocked.AddRange(Enumerable.Repeat(false, this.Ui.Plugin.Config.Tabs.Count));
        }
        
        for (var i = 0; i < this.Ui.Plugin.Config.Tabs.Count; i++) {
            var tab = this.Ui.Plugin.Config.Tabs[i];
            if (!tab.PopOut) {
                continue;
            }
            
            this.DrawPopOut(tab, i);
        }
    }

    private void DrawPopOut(Tab tab, int idx) {
        var flags = ImGuiWindowFlags.None;
        if (!this.Ui.Plugin.Config.ShowPopOutTitleBar) {
            flags |= ImGuiWindowFlags.NoTitleBar;
        }

        if (!this._popOutDocked[idx]) {
            var alpha = tab.IndependentOpacity ? tab.Opacity : this.Ui.Plugin.Config.WindowAlpha;
            ImGui.SetNextWindowBgAlpha(alpha / 100f);
        }

        ImGui.SetNextWindowSize(new Vector2(350, 350) * ImGuiHelpers.GlobalScale, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"{tab.Name}##popout", ref tab.PopOut, flags)) {
            goto End;
        }

        ImGui.PushID($"popout-{tab.Name}");

        if (!this.Ui.Plugin.Config.ShowPopOutTitleBar) {
            ImGui.TextUnformatted(tab.Name);
            ImGui.Separator();
        }

        var handler = this.HandlerLender.Borrow();
        this.DrawMessageLog(tab, handler, ImGui.GetContentRegionAvail().Y, false);

        ImGui.PopID();

        End:
        this._popOutDocked[idx] = ImGui.IsWindowDocked();
        ImGui.End();

        if (!tab.PopOut) {
            this.Ui.Plugin.SaveConfig();
        }
    }

    private unsafe int Callback(ImGuiInputTextCallbackData* data) {
        var ptr = new ImGuiInputTextCallbackDataPtr(data);

        if (data->EventFlag == ImGuiInputTextFlags.CallbackCharFilter) {
            var valid = this.Ui.Plugin.Functions.Chat.IsCharValid((char) ptr.EventChar);
            if (!valid) {
                return 1;
            }
        }

        if (this.Activate) {
            this.Activate = false;
            data->CursorPos = this.Chat.Length;
            data->SelectionStart = data->SelectionEnd = data->CursorPos;
        }

        var text = MemoryHelper.ReadString((IntPtr) data->Buf, data->BufTextLen);
        if (text.StartsWith('/')) {
            var command = text.Split(' ')[0];
            var cmd = this.Ui.Plugin.DataManager.GetExcelSheet<TextCommand>()?.FirstOrDefault(cmd => cmd.Command.RawString == command
                                                                                                     || cmd.Alias.RawString == command
                                                                                                     || cmd.ShortCommand.RawString == command
                                                                                                     || cmd.ShortAlias.RawString == command);
            if (cmd != null) {
                this._commandHelp = new CommandHelp(this, cmd);
                goto PostCommandHelp;
            }
        }

        this._commandHelp = null;

        PostCommandHelp:
        if (data->EventFlag != ImGuiInputTextFlags.CallbackHistory) {
            return 0;
        }

        var prevPos = this._inputBacklogIdx;

        switch (data->EventKey) {
            case ImGuiKey.UpArrow:
                switch (this._inputBacklogIdx) {
                    case -1:
                        var offset = 0;

                        if (!string.IsNullOrWhiteSpace(this.Chat)) {
                            this.AddBacklog(this.Chat);
                            offset = 1;
                        }

                        this._inputBacklogIdx = this._inputBacklog.Count - 1 - offset;
                        break;
                    case > 0:
                        this._inputBacklogIdx--;
                        break;
                }

                break;
            case ImGuiKey.DownArrow: {
                if (this._inputBacklogIdx != -1) {
                    if (++this._inputBacklogIdx >= this._inputBacklog.Count) {
                        this._inputBacklogIdx = -1;
                    }
                }

                break;
            }
        }

        if (prevPos == this._inputBacklogIdx) {
            return 0;
        }

        var historyStr = this._inputBacklogIdx >= 0 ? this._inputBacklog[this._inputBacklogIdx] : string.Empty;

        ptr.DeleteChars(0, ptr.BufTextLen);
        ptr.InsertChars(0, historyStr);

        return 0;
    }

    internal void DrawChunks(IReadOnlyList<Chunk> chunks, bool wrap = true, PayloadHandler? handler = null, float lineWidth = 0f) {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        try {
            for (var i = 0; i < chunks.Count; i++) {
                if (chunks[i] is TextChunk text && string.IsNullOrEmpty(text.Content)) {
                    continue;
                }

                this.DrawChunk(chunks[i], wrap, handler, lineWidth);

                if (i < chunks.Count - 1) {
                    ImGui.SameLine();
                }
            }
        } finally {
            ImGui.PopStyleVar();
        }
    }

    private void DrawChunk(Chunk chunk, bool wrap = true, PayloadHandler? handler = null, float lineWidth = 0f) {
        if (chunk is IconChunk icon && this._fontIcon != null) {
            var bounds = IconUtil.GetBounds((byte) icon.Icon);
            if (bounds != null) {
                var texSize = new Vector2(this._fontIcon.Width, this._fontIcon.Height);

                var sizeRatio = this.Ui.Plugin.Config.FontSize / bounds.Value.W;
                var size = new Vector2(bounds.Value.Z, bounds.Value.W) * sizeRatio * ImGuiHelpers.GlobalScale;

                var uv0 = new Vector2(bounds.Value.X, bounds.Value.Y - 2) / texSize;
                var uv1 = new Vector2(bounds.Value.X + bounds.Value.Z, bounds.Value.Y - 2 + bounds.Value.W) / texSize;
                ImGui.Image(this._fontIcon.ImGuiHandle, size, uv0, uv1);
                ImGuiUtil.PostPayload(chunk, handler);
            }

            return;
        }

        if (chunk is not TextChunk text) {
            return;
        }

        var colour = text.Foreground;
        if (colour == null && text.FallbackColour != null) {
            var type = text.FallbackColour.Value;
            colour = this.Ui.Plugin.Config.ChatColours.TryGetValue(type, out var col)
                ? col
                : type.DefaultColour();
        }

        if (colour != null) {
            colour = ColourUtil.RgbaToAbgr(colour.Value);
            ImGui.PushStyleColor(ImGuiCol.Text, colour.Value);
        }

        var pushed = false;
        if (text.Italic) {
            if (this.Ui.ItalicFont.HasValue && this.Ui.Plugin.Config.FontsEnabled) {
                ImGui.PushFont(this.Ui.ItalicFont.Value);
                pushed = true;
            }

            if (!this.Ui.Plugin.Config.FontsEnabled && this.Ui.AxisItalic.Available) {
                ImGui.PushFont(this.Ui.AxisItalic.ImFont);
                pushed = true;
            }
        }

        var content = text.Content;
        if (this.Ui.ScreenshotMode) {
            if (chunk.Link is PlayerPayload playerPayload) {
                var hashCode = $"{this.Ui.Salt}{playerPayload.PlayerName}{playerPayload.World.RowId}".GetHashCode();
                content = $"Player {hashCode:X8}";
            } else if (this.Ui.Plugin.ClientState.LocalPlayer is { } player && content.Contains(player.Name.TextValue)) {
                var hashCode = $"{this.Ui.Salt}{player.Name.TextValue}{player.HomeWorld.Id}".GetHashCode();
                content = content.Replace(player.Name.TextValue, $"Player {hashCode:X8}");
            }
        }

        if (wrap) {
            ImGuiUtil.WrapText(content, chunk, handler, this.Ui.DefaultText, lineWidth);
        } else {
            ImGui.TextUnformatted(content);
            ImGuiUtil.PostPayload(chunk, handler);
        }

        if (pushed) {
            ImGui.PopFont();
        }

        if (colour != null) {
            ImGui.PopStyleColor();
        }
    }

    private unsafe AddonChatLogPanel* GetChatLogAddon()
    {
        return (AddonChatLogPanel*)GetGenericAddon("ChatLog");
    }

    private unsafe AtkUnitBase* GetGenericAddon(string addon)
    {
        var ptr = Ui.Plugin.GameGui.GetAddonByName(addon, 1);
        if (ptr == IntPtr.Zero)
            return null;
        return (AtkUnitBase*)ptr;
    }

    private unsafe bool CollisionAdjust(AtkUnitBase* atkBase, int childNode = -1)
    {
        /*collisioncheck = (!at->IsVisible && itemdetail->IsVisible */
        if (atkBase == null || !atkBase->IsVisible) return false;

        AtkResNode* atkRes = childNode < 0 ? atkBase->RootNode : atkBase->UldManager.NodeList[childNode];
        bool deferBounds = (atkRes->X == 0 || atkRes->Y == 0);
        return (
        LastWindowPos.X < (!deferBounds ? atkRes->X : atkBase->RootNode->X) + atkRes->Width &&
        LastWindowPos.X + LastWindowSize.X > (!deferBounds ? atkRes->X : atkBase->RootNode->X) &&
        LastWindowPos.Y < (!deferBounds ? atkRes->Y : atkBase->RootNode->Y) + atkRes->Height &&
        LastWindowPos.Y + LastWindowSize.Y > (!deferBounds ? atkRes->Y : atkBase->RootNode->Y));
    }

}
