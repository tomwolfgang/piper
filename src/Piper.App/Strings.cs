namespace Piper.App;

/// <summary>
/// Typed accessors for the string catalogue in <c>Locales/en.json</c>.
/// </summary>
/// <remarks>
/// The text itself lives in the JSON, in the i18next format, so a wording or translation review is
/// a review of one data file. This file holds only the key and the argument list for each entry,
/// which is the same trick a typed i18next setup uses in TypeScript: the call sites keep compile-time
/// checking instead of scattering magic key strings through the UI code, and a key that is renamed
/// in the JSON without being renamed here fails the smoke tests rather than shipping as visible
/// gibberish.
///
/// Adding a string means adding it in both places: the entry in <c>en.json</c>, and the accessor
/// here. Counted messages use the <c>count</c> argument so <see cref="I18n"/> can pick the
/// <c>_one</c> / <c>_other</c> form.
///
/// Deliberately not here: wire-format constants that only look like text -- header names, MIME
/// types, file extensions, registry values, HTTP methods and the search grammar's own field names.
/// Translating one of those would change behaviour rather than wording.
///
/// Menu accelerators belong in <see cref="Shortcuts"/> and are passed to <c>Menus.Item</c>, never
/// embedded in an item's text.
///
/// This file is free of WinForms types so the smoke-test project, which links
/// <c>UpdateService.cs</c> into a plain net10.0 build, can link it too.
/// </remarks>
internal static class Strings
{
    // ---------------------------------------------------------------------- shared

    public static class App
    {
        public static string Name => I18n.T("app.name");
        public static string ClosingTitle => I18n.T("app.closingTitle");
        public static string UnexpectedErrorCaption => I18n.T("app.unexpectedErrorCaption");

        public static string AboutCaption => I18n.T("app.aboutCaption");
        public static string AboutBody => I18n.T("app.aboutBody");

        public static string CrashReport(string type, string message, string? stackTrace) =>
            I18n.T("app.crashReport", ("type", type), ("message", message), ("stackTrace", stackTrace));
    }

    /// <summary>
    /// Accelerator text for the menu's shortcut column. Display only: the key handling itself lives
    /// in the panels' <c>KeyDown</c> handlers and in the menu items' <c>ShortcutKeys</c>.
    /// </summary>
    public static class Shortcuts
    {
        public static string CtrlC => I18n.T("shortcuts.ctrlC");
        public static string CtrlDown => I18n.T("shortcuts.ctrlDown");
        public static string CtrlE => I18n.T("shortcuts.ctrlE");
        public static string CtrlF => I18n.T("shortcuts.ctrlF");
        public static string CtrlR => I18n.T("shortcuts.ctrlR");
        public static string CtrlShiftF => I18n.T("shortcuts.ctrlShiftF");
        public static string CtrlUp => I18n.T("shortcuts.ctrlUp");
        public static string CtrlX => I18n.T("shortcuts.ctrlX");
        public static string Delete => I18n.T("shortcuts.delete");
        public static string F3 => I18n.T("shortcuts.f3");

        public static string ZoomIn => I18n.T("shortcuts.zoomIn");
        public static string ZoomOut => I18n.T("shortcuts.zoomOut");
        public static string ZoomReset => I18n.T("shortcuts.zoomReset");
    }

    /// <summary>Shared message-box captions and the buttons that appear on more than one dialog.</summary>
    public static class Common
    {
        public static string Save => I18n.T("common.save");
        public static string Cancel => I18n.T("common.cancel");

        public static string AllFilesFilter => I18n.T("common.allFilesFilter");
    }

    /// <summary>
    /// Transfer sizes. The thresholds stay with each caller; only the wording is shared, because
    /// the three size formatters round to different precisions on purpose.
    /// </summary>
    public static class Units
    {
        public static string Bytes(long value) => I18n.T("units.bytes", ("value", value));
        public static string Kilobytes(double value) => I18n.T("units.kilobytes", ("value", value));
        public static string Megabytes(double value) => I18n.T("units.megabytes", ("value", value));

        /// <summary>Shown in the capture grid's Size column when there was no response body.</summary>
        public static string NoBytes => I18n.T("units.noBytes");

        // A body still arriving, as "received/total" in the unit of the total. Compact, because it
        // has to fit the grid's Size column.
        public static string ProgressBytes(long received, long total) =>
            I18n.T("units.progressBytes", ("received", received), ("total", total));
        public static string ProgressKilobytes(double received, double total) =>
            I18n.T("units.progressKilobytes", ("received", received), ("total", total));
        public static string ProgressMegabytes(double received, double total) =>
            I18n.T("units.progressMegabytes", ("received", received), ("total", total));
        public static string ProgressGigabytes(double received, double total) =>
            I18n.T("units.progressGigabytes", ("received", received), ("total", total));

        /// <summary>The long form of a body's progress, for the inspector and the status bar.</summary>
        public static string OfTotal(string received, string total, double fraction) =>
            I18n.T("units.ofTotal", ("received", received), ("total", total), ("fraction", fraction));
    }

    // ----------------------------------------------------------------------- shell

    public static class Tabs
    {
        public static string Inspectors => I18n.T("tabs.inspectors");
        public static string Composer => I18n.T("tabs.composer");
        public static string Filters => I18n.T("tabs.filters");
        public static string AutoResponder => I18n.T("tabs.autoResponder");
        public static string Log => I18n.T("tabs.log");
    }

    public static class Menu
    {
        public static string File => I18n.T("menu.file");
        public static string OpenSaz => I18n.T("menu.openSaz");
        public static string SaveSaz => I18n.T("menu.saveSaz");
        public static string ClearSessions => I18n.T("menu.clearSessions");
        public static string Exit => I18n.T("menu.exit");

        public static string Tools => I18n.T("menu.tools");
        public static string Configurations => I18n.T("menu.configurations");
        public static string Hosts => I18n.T("menu.hosts");
        public static string FindSessions => I18n.T("menu.findSessions");
        public static string TextWizard => I18n.T("menu.textWizard");

        public static string Help => I18n.T("menu.help");
        public static string SearchSyntax => I18n.T("menu.searchSyntax");
        public static string CheckForUpdates => I18n.T("menu.checkForUpdates");
        public static string SaveDiagnostics => I18n.T("menu.saveDiagnostics");
        public static string About => I18n.T("menu.about");

        public static string View => I18n.T("menu.view");
        public static string ZoomIn => I18n.T("menu.zoomIn");
        public static string ZoomOut => I18n.T("menu.zoomOut");
        public static string ResetZoom => I18n.T("menu.resetZoom");
        public static string ResetZoomTo(int percent) => I18n.T("menu.resetZoomTo", ("percent", percent));

        public static string Rules => I18n.T("menu.rules");
        public static string UserAgent => I18n.T("menu.userAgent");
        public static string UserAgentNoOverride => I18n.T("menu.userAgentNoOverride");
        public static string UserAgentCustom => I18n.T("menu.userAgentCustom");
        public static string EnableAutomaticResponses => I18n.T("menu.enableAutomaticResponses");
        public static string UnmatchedRequestsPassThrough => I18n.T("menu.unmatchedRequestsPassThrough");
        public static string AutoResponderRules => I18n.T("menu.autoResponderRules");
    }

    public static class Toolbar
    {
        public static string Clear => I18n.T("toolbar.clear");
        public static string Composer => I18n.T("toolbar.composer");

        public static string DarkMode => I18n.T("toolbar.darkMode");
        public static string LightMode => I18n.T("toolbar.lightMode");
        public static string ThemeToggle(string target) => I18n.T("toolbar.themeToggle", ("target", target));

        /// <summary>The case change stays here: casing rules are per-language, not per-template.</summary>
        public static string ThemeToggleTooltip(string target) =>
            I18n.T("toolbar.themeToggleTooltip", ("target", target.ToLowerInvariant()));
    }

    public static class StatusBar
    {
        public static string Starting => I18n.T("statusBar.starting");
        public static string PleaseWait => I18n.T("statusBar.pleaseWait");

        public static string Capturing => I18n.T("statusBar.capturing");
        public static string NotCapturing => I18n.T("statusBar.notCapturing");
        public static string NotCapturingStatus => I18n.T("statusBar.notCapturingStatus");
        public static string EnablingProxy => I18n.T("statusBar.enablingProxy");
        public static string DisablingProxy => I18n.T("statusBar.disablingProxy");
        public static string RestoringSystemProxy => I18n.T("statusBar.restoringSystemProxy");
        public static string StoppingCapture => I18n.T("statusBar.stoppingCapture");

        public static string CaptureTooltip => I18n.T("statusBar.captureTooltip");
        public static string ScopeTooltip => I18n.T("statusBar.scopeTooltip");
        public static string BreakpointsNone => I18n.T("statusBar.breakpointsNone");
        public static string BreakpointsTooltip => I18n.T("statusBar.breakpointsTooltip");
        public static string NoSessions => I18n.T("statusBar.noSessions");
        public static string SelectedSessionTooltip => I18n.T("statusBar.selectedSessionTooltip");
        public static string ZoomTooltip => I18n.T("statusBar.zoomTooltip");

        public static string Listening(object? endpoint, bool decryptHttps) =>
            I18n.T("statusBar.listening", ("endpoint", endpoint),
                ("state", decryptHttps ? I18n.T("statusBar.httpsOn") : I18n.T("statusBar.httpsOff")));

        public static string Sessions(int total) => I18n.T("statusBar.sessions", ("count", total));

        public static string Sessions(int selected, int total) =>
            I18n.T("statusBar.sessionsSelected", ("selected", selected), ("count", total));

        public static string Zoom(int percent) => I18n.T("statusBar.zoom", ("percent", percent));
    }

    /// <summary>Capture-scope names, shared by the status-bar menu and the Configurations dialog.</summary>
    public static class CaptureScopes
    {
        public static string AllProcesses => I18n.T("captureScopes.allProcesses");
        public static string WebBrowsers => I18n.T("captureScopes.webBrowsers");
        public static string NonBrowsers => I18n.T("captureScopes.nonBrowsers");
        public static string HideAll => I18n.T("captureScopes.hideAll");
    }

    public static class UserAgents
    {
        public static string ChromeWindows => I18n.T("userAgents.chromeWindows");
        public static string EdgeWindows => I18n.T("userAgents.edgeWindows");
        public static string FirefoxWindows => I18n.T("userAgents.firefoxWindows");
        public static string ChromeAndroid => I18n.T("userAgents.chromeAndroid");
        public static string SafariIPhone => I18n.T("userAgents.safariIPhone");

        /// <summary>The name recorded in the log when the User-Agent came from the custom prompt.</summary>
        public static string CustomName => I18n.T("userAgents.customName");

        public static string PromptCaption => I18n.T("userAgents.promptCaption");
        public static string PromptLabel => I18n.T("userAgents.promptLabel");
    }

    // ------------------------------------------------------------------ main form

    public static class SazImport
    {
        public static string OpenCaption => I18n.T("sazImport.openCaption");
        public static string OpenFilter => I18n.T("sazImport.openFilter");

        public static string SaveCaption => I18n.T("sazImport.saveCaption");
        public static string SaveFilter => I18n.T("sazImport.saveFilter");
        public static string SuggestedFileName(DateTime now) => I18n.T("sazImport.suggestedFileName", ("now", now));
    }

    public static class Certificates
    {
        public static string StartupTrustCaption => I18n.T("certificates.startupTrustCaption");
        public static string StartupTrustBody => I18n.T("certificates.startupTrustBody");

        public static string StartupInstallFailed(string message) =>
            I18n.T("certificates.startupInstallFailed", ("message", message));

        public static string AlreadyTrusted => I18n.T("certificates.alreadyTrusted");
        public static string TrustCaption => I18n.T("certificates.trustCaption");

        public static string TrustBody(string privateKeyPath, string? thumbprint) =>
            I18n.T("certificates.trustBody", ("privateKeyPath", privateKeyPath), ("thumbprint", thumbprint));

        public static string Removed(int count) => I18n.T("certificates.removed", ("count", count));

        public static string ExportCaption => I18n.T("certificates.exportCaption");
        public static string ExportFilter => I18n.T("certificates.exportFilter");
        public static string ExportFileName => I18n.T("certificates.exportFileName");
    }

    public static class Updates
    {
        public static string CheckCaption => I18n.T("updates.checkCaption");
        public static string InstallCaption => I18n.T("updates.installCaption");
        public static string AvailableCaption => I18n.T("updates.availableCaption");

        public static string AlreadyChecking => I18n.T("updates.alreadyChecking");
        public static string UpToDate(Version version) => I18n.T("updates.upToDate", ("version", version));
        public static string AvailableBody(Version version) => I18n.T("updates.availableBody", ("version", version));

        public static string NoResponse => I18n.T("updates.noResponse");
        public static string HttpStatus(int statusCode) => I18n.T("updates.httpStatus", ("statusCode", statusCode));
        public static string ResponseTooLarge => I18n.T("updates.responseTooLarge");
        public static string NoReleaseTag => I18n.T("updates.noReleaseTag");
        public static string UnsupportedVersion => I18n.T("updates.unsupportedVersion");
        public static string NoAssets => I18n.T("updates.noAssets");
        public static string MissingInstallerOrManifest => I18n.T("updates.missingInstallerOrManifest");
        public static string InvalidMetadata => I18n.T("updates.invalidMetadata");

        public static string ManifestMissingInstaller => I18n.T("updates.manifestMissingInstaller");
        public static string ChecksumMismatch => I18n.T("updates.checksumMismatch");
        public static string DownloadCancelled => I18n.T("updates.downloadCancelled");
        public static string DownloadTimedOut => I18n.T("updates.downloadTimedOut");
        public static string DownloadFailed(string message) => I18n.T("updates.downloadFailed", ("message", message));
        public static string InstallerTooLarge => I18n.T("updates.installerTooLarge");
        public static string ManifestTooLarge => I18n.T("updates.manifestTooLarge");
    }

    public static class SearchHelp
    {
        public static string Caption => I18n.T("searchHelp.caption");
        public static string Body => I18n.T("searchHelp.body");
    }

    /// <summary>Lines written to the Log tab.</summary>
    public static class Log
    {
        public static string AnalyticsOn => I18n.T("log.analyticsOn");
        public static string AnalyticsOff => I18n.T("log.analyticsOff");
        public static string AnalyticsOffIdentifierKept => I18n.T("log.analyticsOffIdentifierKept");
        public static string AnalyticsUnavailable => I18n.T("log.analyticsUnavailable");
        public static string AnalyticsConsentOn => I18n.T("log.analyticsConsentOn");
        public static string AnalyticsConsentOff => I18n.T("log.analyticsConsentOff");

        public static string RootCaPath(string path) => I18n.T("log.rootCaPath", ("path", path));
        public static string RootCaTrusted => I18n.T("log.rootCaTrusted");
        public static string RootCaNotTrusted => I18n.T("log.rootCaNotTrusted");
        public static string UpstreamValidationOffAtStartup => I18n.T("log.upstreamValidationOffAtStartup");
        public static string UpstreamValidationOffAfterSave => I18n.T("log.upstreamValidationOffAfterSave");

        public static string RestoredLeftoverProxy(string endpoint) =>
            I18n.T("log.restoredLeftoverProxy", ("endpoint", endpoint));
        public static string StartupCaptureOff => I18n.T("log.startupCaptureOff");
        public static string RootTrusted(string? thumbprint) => I18n.T("log.rootTrusted", ("thumbprint", thumbprint));
        public static string TrustRootFailed(string message) => I18n.T("log.trustRootFailed", ("message", message));
        public static string TrustingRootFailed(string message) => I18n.T("log.trustingRootFailed", ("message", message));
        public static string RootsRemoved(int count) => I18n.T("log.rootsRemoved", ("count", count));
        public static string RootExported(string path) => I18n.T("log.rootExported", ("path", path));

        public static string GlobalUserAgentCleared => I18n.T("log.globalUserAgentCleared");
        public static string GlobalUserAgentSet(string name) => I18n.T("log.globalUserAgentSet", ("name", name));

        public static string ConfigurationsSaved => I18n.T("log.configurationsSaved");
        public static string HostRemappingEnabled => I18n.T("log.hostRemappingEnabled");
        public static string HostRemappingDisabled => I18n.T("log.hostRemappingDisabled");

        public static string ImportedSessions(int count, string fileName, bool intoComposer) => intoComposer
            ? I18n.T("log.importedSessionsToComposer", ("count", count), ("fileName", fileName))
            : I18n.T("log.importedSessions", ("count", count), ("fileName", fileName));

        public static string SazImportWarning(string fileName, string warning) =>
            I18n.T("log.sazImportWarning", ("fileName", fileName), ("warning", warning));
        public static string AutoResponderWarning(string warning) =>
            I18n.T("log.autoResponderWarning", ("warning", warning));

        public static string ListenFailed(int port, string exceptionType, string message) =>
            I18n.T("log.listenFailed", ("port", port), ("exceptionType", exceptionType), ("message", message));
        public static string PortInUseHint => I18n.T("log.portInUseHint");
        public static string CaptureStoppedProxyRestored => I18n.T("log.captureStoppedProxyRestored");
        public static string CaptureStateFailed(string message) => I18n.T("log.captureStateFailed", ("message", message));
        public static string CaptureScopeSet(string scope) => I18n.T("log.captureScopeSet", ("scope", scope));

        public static string SystemProxySet(string endpoint) => I18n.T("log.systemProxySet", ("endpoint", endpoint));
        public static string PartialChangeUndoFailed(string message) =>
            I18n.T("log.partialChangeUndoFailed", ("message", message));
        public static string SystemProxyFailed(string message) => I18n.T("log.systemProxyFailed", ("message", message));
        public static string SystemProxyRestoreFailed(string message) =>
            I18n.T("log.systemProxyRestoreFailed", ("message", message));
        public static string ShutdownFailed(string message) => I18n.T("log.shutdownFailed", ("message", message));

        public static string HideHostNotFilterable => I18n.T("log.hideHostNotFilterable");
        public static string HideHostShowOnlyConflict(string host) =>
            I18n.T("log.hideHostShowOnlyConflict", ("host", host));
        public static string HideHostSwitchedToHideMode => I18n.T("log.hideHostSwitchedToHideMode");
        public static string HideHostAdded(string host) => I18n.T("log.hideHostAdded", ("host", host));
        public static string SessionHiddenHostsShown => I18n.T("log.sessionHiddenHostsShown");

        public static string ShowingTrustPrompt => I18n.T("log.showingTrustPrompt");
        public static string NoSazToImport(string names) => I18n.T("log.noSazToImport", ("names", names));
        public static string RunningElevated => I18n.T("log.runningElevated");
        public static string WritingDiagnostics(string fileName) =>
            I18n.T("log.writingDiagnostics", ("fileName", fileName));
        public static string DiagnosticsWriteFailed(string message) =>
            I18n.T("log.diagnosticsWriteFailed", ("message", message));

        public static string IgnoredDrop(string reason) => I18n.T("log.ignoredDrop", ("reason", reason));
        public static string DropNoData => I18n.T("log.dropNoData");
        public static string DropNoRecognisedFormat => I18n.T("log.dropNoRecognisedFormat");
        public static string DropNoFileOnly(string formats) => I18n.T("log.dropNoFileOnly", ("formats", formats));
        public static string DropNoneImportable(int count, string names) =>
            I18n.T("log.dropNoneImportable", ("count", count), ("names", names));
        public static string DropRejectedFolder(string name) => I18n.T("log.dropRejectedFolder", ("name", name));
        public static string DropRejectedMissing(string name) => I18n.T("log.dropRejectedMissing", ("name", name));
        public static string DropRejectedWrongType(string name) =>
            I18n.T("log.dropRejectedWrongType", ("name", name));

        public static string UpdateCheckFailed(string message) => I18n.T("log.updateCheckFailed", ("message", message));
        public static string UpToDate(Version version) => I18n.T("log.upToDate", ("version", version));
        public static string UpdateAvailable(Version version) => I18n.T("log.updateAvailable", ("version", version));
        public static string DownloadingInstaller(Version version) =>
            I18n.T("log.downloadingInstaller", ("version", version));
        public static string UpdateDownloadFailed(string? message) =>
            I18n.T("log.updateDownloadFailed", ("message", message));
        public static string InstallerStarted(Version version) => I18n.T("log.installerStarted", ("version", version));
        public static string InstallerStartFailed(string message) =>
            I18n.T("log.installerStartFailed", ("message", message));

        /// <summary>The trailing newline is layout, not copy, so it stays out of the catalogue.</summary>
        public static string Line(DateTime now, string message) =>
            I18n.T("log.line", ("time", now), ("message", message)) + Environment.NewLine;
    }

    public static class Shutdown
    {
        public static string ProxyRestoreFailed(string message) =>
            I18n.T("shutdown.proxyRestoreFailed", ("message", message));
    }

    /// <summary>
    /// The bug-report bundle. Its wording states exactly what the export contains, so treat a change
    /// here as a change to a promise about captured data rather than as copy editing.
    /// </summary>
    public static class Diagnostics
    {
        public static string SaveCaption => I18n.T("diagnostics.saveCaption");
        public static string SaveFilter => I18n.T("diagnostics.saveFilter");
        public static string SaveFileName(DateTime now) => I18n.T("diagnostics.saveFileName", ("now", now));

        public static string WriteFailedCaption => I18n.T("diagnostics.writeFailedCaption");
        public static string WriteFailedBody(string message) =>
            I18n.T("diagnostics.writeFailedBody", ("message", message));

        /// <summary>What the bundle actually holds, quoted back to the user in the saved dialog.</summary>
        public static string Contents => I18n.T("diagnostics.contents");
        public static string SummaryNone => I18n.T("diagnostics.summaryNone");
        public static string SummaryAndMore(string shown, int more) =>
            I18n.T("diagnostics.summaryAndMore", ("shown", shown), ("more", more));
        public static string EarlierEntriesOmitted => I18n.T("diagnostics.earlierEntriesOmitted");
        public static string CrashLogUnreadable(string fileName, string message) =>
            I18n.T("diagnostics.crashLogUnreadable", ("fileName", fileName), ("message", message));

        public static string SavedCaption => I18n.T("diagnostics.savedCaption");
        public static string SavedBody(string fileName, string contents) =>
            I18n.T("diagnostics.savedBody", ("fileName", fileName), ("contents", contents));

        public static string Environment(string? version, string os, object architecture, object runtime,
            bool is64Bit, bool elevated, int scale, string culture) =>
            I18n.T("diagnostics.environment",
                ("version", version ?? I18n.T("diagnostics.versionUnknown")),
                ("os", os), ("architecture", architecture), ("runtime", runtime),
                ("bitness", is64Bit ? I18n.T("diagnostics.bits64") : I18n.T("diagnostics.bits32")),
                ("elevated", elevated ? I18n.T("diagnostics.yes") : I18n.T("diagnostics.no")),
                ("scale", scale), ("culture", culture));
    }

    // ------------------------------------------------------------------ capture list

    public static class SessionList
    {
        public static string FilterPlaceholder => I18n.T("sessionList.filterPlaceholder");

        public static string ColumnId => I18n.T("sessionList.columnId");
        public static string ColumnResult => I18n.T("sessionList.columnResult");
        public static string ColumnMethod => I18n.T("sessionList.columnMethod");
        public static string ColumnHost => I18n.T("sessionList.columnHost");
        public static string ColumnPath => I18n.T("sessionList.columnPath");
        public static string ColumnType => I18n.T("sessionList.columnType");
        public static string ColumnProcess => I18n.T("sessionList.columnProcess");
        public static string ColumnSize => I18n.T("sessionList.columnSize");
        public static string ColumnTime => I18n.T("sessionList.columnTime");

        /// <summary>Method column text for an undecrypted CONNECT tunnel.</summary>
        public static string TunnelMethod => I18n.T("sessionList.tunnelMethod");
        /// <summary>Process column text when the owning process is unknown.</summary>
        public static string UnknownProcess => I18n.T("sessionList.unknownProcess");
        /// <summary>Time column text while a session is still in flight.</summary>
        public static string PendingDuration => I18n.T("sessionList.pendingDuration");
        public static string Duration(double milliseconds) =>
            I18n.T("sessionList.duration", ("milliseconds", milliseconds));
        /// <summary>Result column text while the response body is still arriving.</summary>
        public static string ReceivingResult(string status) =>
            I18n.T("sessionList.receivingResult", ("status", status));

        public static string Resend => I18n.T("sessionList.resend");
        public static string SendToComposer => I18n.T("sessionList.sendToComposer");
        public static string CreateAutoResponderRule => I18n.T("sessionList.createAutoResponderRule");
        public static string CopyUrl => I18n.T("sessionList.copyUrl");
        public static string CopyAsCurl => I18n.T("sessionList.copyAsCurl");
        public static string CopyFullSession => I18n.T("sessionList.copyFullSession");
        public static string SaveMenu => I18n.T("sessionList.saveMenu");
        public static string SaveResponseBody => I18n.T("sessionList.saveResponseBody");
        public static string SaveSessionsAsSaz => I18n.T("sessionList.saveSessionsAsSaz");
        public static string FindSessions => I18n.T("sessionList.findSessions");
        public static string FindNext => I18n.T("sessionList.findNext");
        public static string ClearFindMarks => I18n.T("sessionList.clearFindMarks");
        public static string FilterSessions => I18n.T("sessionList.filterSessions");
        public static string FilterToThisHost => I18n.T("sessionList.filterToThisHost");
        public static string HideThisHost => I18n.T("sessionList.hideThisHost");
        public static string ShowHiddenHosts => I18n.T("sessionList.showHiddenHosts");
        public static string FollowTail => I18n.T("sessionList.followTail");
        public static string FollowTailTooltip => I18n.T("sessionList.followTailTooltip");
        public static string SendUrlToTextWizard => I18n.T("sessionList.sendUrlToTextWizard");
        public static string RemoveSelected => I18n.T("sessionList.removeSelected");

        public static string SaveResponseBodyCaption => I18n.T("sessionList.saveResponseBodyCaption");
        public static string SaveResponseBodyFailed(string message) =>
            I18n.T("sessionList.saveResponseBodyFailed", ("message", message));
        public static string SaveSessionsFailed(string message) =>
            I18n.T("sessionList.saveSessionsFailed", ("message", message));
        public static string SuggestedResponseFileName(int sessionId, string extension) =>
            I18n.T("sessionList.suggestedResponseFileName", ("sessionId", sessionId), ("extension", extension));

        public static string FindCaption => I18n.T("sessionList.findCaption");
        public static string FindNothingToMatch => I18n.T("sessionList.findNothingToMatch");
        public static string FindNoMatches => I18n.T("sessionList.findNoMatches");
        public static string RegexTimedOut => I18n.T("sessionList.regexTimedOut");
        public static string FindMarksRemoved(int count) => I18n.T("sessionList.findMarksRemoved", ("count", count));
        public static string FindMarked(int count) => I18n.T("sessionList.findMarked", ("count", count));
        public static string FindSelectionCapped(int limit) =>
            I18n.T("sessionList.findSelectionCapped", ("limit", limit));
    }

    public static class FindSessions
    {
        public static string Caption => I18n.T("findSessions.caption");
        public static string FindButton => I18n.T("findSessions.findButton");

        public static string FindLabel => I18n.T("findSessions.findLabel");
        public static string SearchLabel => I18n.T("findSessions.searchLabel");
        public static string MarkLabel => I18n.T("findSessions.markLabel");
        public static string SelectMatches => I18n.T("findSessions.selectMatches");

        public static string Hint => I18n.T("findSessions.hint");

        public static string Yellow => I18n.T("findSessions.yellow");
        public static string Orange => I18n.T("findSessions.orange");
        public static string Red => I18n.T("findSessions.red");
        public static string Green => I18n.T("findSessions.green");
        public static string Blue => I18n.T("findSessions.blue");
        public static string Purple => I18n.T("findSessions.purple");
        public static string Gray => I18n.T("findSessions.gray");
        public static string NoHighlight => I18n.T("findSessions.noHighlight");
    }

    // -------------------------------------------------------------------- inspector

    public static class Inspector
    {
        public static string Request => I18n.T("inspector.request");
        public static string Response => I18n.T("inspector.response");

        public static string RequestSummary(string startLine) => I18n.T("inspector.requestSummary", ("startLine", startLine));
        public static string ResponseSummary(string startLine) => I18n.T("inspector.responseSummary", ("startLine", startLine));
        public static string ResponseFailed(string? error, string hint) =>
            I18n.T("inspector.responseFailed", ("error", error), ("hint", hint));
        public static string ResponseTunnel => I18n.T("inspector.responseTunnel");
        public static string ResponseWaiting => I18n.T("inspector.responseWaiting");
        public static string ResponseReceiving(string startLine, string progress) =>
            I18n.T("inspector.responseReceiving", ("startLine", startLine), ("progress", progress));

        /// <summary>
        /// The status bar's timing summary, which is assembled from the parts that apply to a given
        /// session. Each part carries its own leading separator, so the spacing of the whole line
        /// reads in the catalogue rather than at the builder that joins them.
        /// </summary>
        public static string TimingStarted(DateTimeOffset time) => I18n.T("inspector.timingStarted", ("time", time));
        public static string TimingTotal(double milliseconds) =>
            I18n.T("inspector.timingTotal", ("milliseconds", milliseconds));
        public static string TimingConnect(double milliseconds) =>
            I18n.T("inspector.timingConnect", ("milliseconds", milliseconds));
        public static string TimingTimeToFirstByte(double milliseconds) =>
            I18n.T("inspector.timingTimeToFirstByte", ("milliseconds", milliseconds));
        public static string TimingUp(string size) => I18n.T("inspector.timingUp", ("size", size));
        public static string TimingDown(string size) => I18n.T("inspector.timingDown", ("size", size));
        public static string TimingServer(object endpoint) => I18n.T("inspector.timingServer", ("endpoint", endpoint));
        public static string TimingComposed => I18n.T("inspector.timingComposed");

        public static string TabHeaders => I18n.T("inspector.tabHeaders");
        public static string TabBody => I18n.T("inspector.tabBody");
        public static string TabRaw => I18n.T("inspector.tabRaw");
        public static string TabHex => I18n.T("inspector.tabHex");
        public static string TabJson => I18n.T("inspector.tabJson");
        public static string TabWebForms => I18n.T("inspector.tabWebForms");
        public static string TabImage => I18n.T("inspector.tabImage");
        public static string TabVideo => I18n.T("inspector.tabVideo");

        public static string ColumnName => I18n.T("inspector.columnName");
        public static string ColumnValue => I18n.T("inspector.columnValue");
        public static string ColumnSource => I18n.T("inspector.columnSource");
        public static string ColumnContentType => I18n.T("inspector.columnContentType");

        public static string SearchHeaders => I18n.T("inspector.searchHeaders");
        public static string SearchJson => I18n.T("inspector.searchJson");
        public static string FindTextOrBytes => I18n.T("inspector.findTextOrBytes");
        public static string HexToggle => I18n.T("inspector.hexToggle");
        public static string Force => I18n.T("inspector.force");
        /// <summary>Glyph on the previous-match button.</summary>
        public static string PreviousMatch => I18n.T("inspector.previousMatch");
        /// <summary>Glyph on the next-match button.</summary>
        public static string NextMatch => I18n.T("inspector.nextMatch");

        public static string CopyEntireHeader => I18n.T("inspector.copyEntireHeader");
        public static string CopyValueOnly => I18n.T("inspector.copyValueOnly");
        public static string OpenWithDefaultBrowser => I18n.T("inspector.openWithDefaultBrowser");
        public static string SendValueToTextWizard => I18n.T("inspector.sendValueToTextWizard");
        public static string CopyKeyValue => I18n.T("inspector.copyKeyValue");
        public static string CopyNameValue => I18n.T("inspector.copyNameValue");
        public static string CopyValue => I18n.T("inspector.copyValue");
        public static string SaveBinaryData => I18n.T("inspector.saveBinaryData");
        public static string ViewInHex => I18n.T("inspector.viewInHex");
        public static string CopySelection => I18n.T("inspector.copySelection");
        public static string CopySelectionAsHex => I18n.T("inspector.copySelectionAsHex");
        public static string SelectAll => I18n.T("inspector.selectAll");
        public static string Find => I18n.T("inspector.find");

        public static string ZoomIn => I18n.T("inspector.zoomIn");
        public static string ZoomOut => I18n.T("inspector.zoomOut");
        public static string ActualSize => I18n.T("inspector.actualSize");
        public static string ZoomInButton => I18n.T("inspector.zoomInButton");
        public static string ZoomOutButton => I18n.T("inspector.zoomOutButton");
        public static string ActualSizeButton => I18n.T("inspector.actualSizeButton");
        public static string ActualSizeTooltip => I18n.T("inspector.actualSizeTooltip");
        public static string SaveImageMenu => I18n.T("inspector.saveImageMenu");
        public static string SaveImageTooltip => I18n.T("inspector.saveImageTooltip");
        public static string SaveAsPng => I18n.T("inspector.saveAsPng");
        public static string SaveAsJpeg => I18n.T("inspector.saveAsJpeg");
        public static string SaveAsWebp => I18n.T("inspector.saveAsWebp");
        public static string SaveImageAs => I18n.T("inspector.saveImageAs");

        public static string SaveImageCaption => I18n.T("inspector.saveImageCaption");
        public static string SaveImageFileName(string extension) =>
            I18n.T("inspector.saveImageFileName", ("extension", extension));
        public static string SaveImageFilter => I18n.T("inspector.saveImageFilter");
        public static string SaveImageFailed(string message) => I18n.T("inspector.saveImageFailed", ("message", message));

        public static string SaveFormFieldCaption => I18n.T("inspector.saveFormFieldCaption");
        public static string SaveFormFieldFailedCaption => I18n.T("inspector.saveFormFieldFailedCaption");
        public static string FormFieldFallbackFileName => I18n.T("inspector.formFieldFallbackFileName");
        public static string HexViewerCaption(string fieldName) =>
            I18n.T("inspector.hexViewerCaption", ("fieldName", fieldName));

        public static string NoWebFormFields => I18n.T("inspector.noWebFormFields");
        public static string WebFormFieldCount(int count) => I18n.T("inspector.webFormFieldCount", ("count", count));
        public static string WebFormParseFailed(string message) =>
            I18n.T("inspector.webFormParseFailed", ("message", message));

        public static string TransferSummary(string download, string decoded) =>
            I18n.T("inspector.transferSummary", ("download", download), ("decoded", decoded));

        public static string NotAnImageContentType => I18n.T("inspector.notAnImageContentType");
        public static string DetectedImage => I18n.T("inspector.detectedImage");
        public static string ImageDimensions(int width, int height, string label) =>
            I18n.T("inspector.imageDimensions", ("width", width), ("height", height), ("label", label));
        public static string ImageDecodeFailed => I18n.T("inspector.imageDecodeFailed");
        public static string ImageFailed(string message) => I18n.T("inspector.imageFailed", ("message", message));

        public static string NotAVideoContentType => I18n.T("inspector.notAVideoContentType");
        public static string DetectedVideo => I18n.T("inspector.detectedVideo");
        public static string VideoLoadedNoContentType => I18n.T("inspector.videoLoadedNoContentType");
        public static string VideoLoading(string? contentType) =>
            I18n.T("inspector.videoLoading", ("contentType", contentType ?? DetectedVideo));
        public static string VideoFailed(string message) => I18n.T("inspector.videoFailed", ("message", message));

        public static string NoBody => I18n.T("inspector.noBody");
        public static string Truncated => I18n.T("inspector.truncated");
        public static string BinaryContent(long bytes) => I18n.T("inspector.binaryContent", ("bytes", bytes));
        public static string BinaryBody(long bytes, string? contentType) =>
            I18n.T("inspector.binaryBody", ("bytes", bytes), ("contentType", contentType ?? BinaryContentType));
        public static string BinaryJsonBody(long bytes, string? contentType) =>
            I18n.T("inspector.binaryJsonBody", ("bytes", bytes), ("contentType", contentType ?? BinaryContentType));
        public static string BodyDecodeFailed(string message) =>
            I18n.T("inspector.bodyDecodeFailed", ("message", message));
        public static string BodyNotFullyCaptured(long kept, long total) =>
            I18n.T("inspector.bodyNotFullyCaptured", ("kept", kept), ("total", total));
        public static string BodyReleased(long total) =>
            I18n.T("inspector.bodyReleased", ("total", total));
        public static string NotAJsonContentType => I18n.T("inspector.notAJsonContentType");
        public static string JsonSkipped => I18n.T("inspector.jsonSkipped");
        public static string NotValidJson(string message) => I18n.T("inspector.notValidJson", ("message", message));

        private static string BinaryContentType => I18n.T("inspector.binaryContentTypeFallback");

        public static string InvalidHex => I18n.T("inspector.invalidHex");
        public static string NotFound => I18n.T("inspector.notFound");
        public static string FoundAt(long position) => I18n.T("inspector.foundAt", ("position", position));
        public static string MatchCount(int count) => I18n.T("inspector.matchCount", ("count", count));
        public static string MatchPosition(int index, int total) =>
            I18n.T("inspector.matchPosition", ("index", index), ("total", total));
        public static string HeaderMatchCount(int visible, int total) =>
            I18n.T("inspector.headerMatchCount", ("visible", visible), ("total", total));
    }

    public static class CertificateHint
    {
        public static string VerifyOriginCertificates => I18n.T("certificateHint.verifyOriginCertificates");
    }

    // --------------------------------------------------------------------- composer

    public static class Composer
    {
        public static string HistoryHeader => I18n.T("composer.historyHeader");
        public static string SearchPlaceholder => I18n.T("composer.searchPlaceholder");
        public static string SearchTooltip => I18n.T("composer.searchTooltip");

        /// <summary>Two counts in one sentence, so each is pluralised on its own before composing.</summary>
        public static string HistoryCount(int sends, int hosts) =>
            I18n.T("composer.historyCount", ("requests", Requests(sends)), ("hosts", Hosts(hosts)));

        public static string HistoryCountWithWarning(int sends, string warning) =>
            I18n.T("composer.historyCountWithWarning", ("requests", Requests(sends)), ("warning", warning));

        /// <summary>The method and target of one history row, as accessibility tools read it.</summary>
        public static string HistoryRowLabel(string method, string target) =>
            I18n.T("composer.historyRowLabel", ("method", method), ("target", target));

        private static string Requests(int count) => I18n.T("composer.requests", ("count", count));
        private static string Hosts(int count) => I18n.T("composer.hosts", ("count", count));

        public static string RemoveFromHistory => I18n.T("composer.removeFromHistory");
        public static string RemoveGroupOneHost(string host) => I18n.T("composer.removeGroupOneHost", ("host", host));
        public static string RemoveGroupManyHosts(int hosts) =>
            I18n.T("composer.removeGroupManyHosts", ("count", hosts));
        public static string ConfirmRemoveGroup(string what, int sends) =>
            I18n.T("composer.confirmRemoveGroup", ("what", what), ("requests", Requests(sends)));

        public static string UrlPlaceholder => I18n.T("composer.urlPlaceholder");
        public static string DefaultHeaders => I18n.T("composer.defaultHeaders");
        public static string BodyHeader => I18n.T("composer.bodyHeader");
        public static string TabHeaders => I18n.T("composer.tabHeaders");
        public static string TabRaw => I18n.T("composer.tabRaw");
        public static string Send => I18n.T("composer.send");
        public static string CancelSend => I18n.T("composer.cancelSend");

        public static string EnterUrl => I18n.T("composer.enterUrl");
        public static string InvalidUrl(string url) => I18n.T("composer.invalidUrl", ("url", url));
        public static string RawNotApplied(string error) => I18n.T("composer.rawNotApplied", ("error", error));

        public static string ResponseNotSent => I18n.T("composer.responseNotSent");
        public static string ResponseCancelled => I18n.T("composer.responseCancelled");
        public static string LoadedSession(int sessionId) => I18n.T("composer.loadedSession", ("sessionId", sessionId));
        public static string Sending => I18n.T("composer.sending");
        public static string Cancelled => I18n.T("composer.cancelled");
        public static string Failed(string? error, string hint) =>
            I18n.T("composer.failed", ("error", error), ("hint", hint));
        public static string FailedSummary(string? error, string hint) =>
            I18n.T("composer.failedSummary", ("error", error), ("hint", hint));
        public static string Error(string message) => I18n.T("composer.error", ("message", message));
        public static string ErrorSummary(string message) => I18n.T("composer.errorSummary", ("message", message));
        public static string Result(int sessionId, int statusCode, double milliseconds, string size) =>
            I18n.T("composer.result", ("sessionId", sessionId), ("statusCode", statusCode),
                ("milliseconds", milliseconds), ("size", size));
        public static string ResultSummary(string? startLine, double milliseconds, string size) =>
            I18n.T("composer.resultSummary", ("startLine", startLine),
                ("milliseconds", milliseconds), ("size", size));
    }

    // ---------------------------------------------------------------------- filters

    public static class Filters
    {
        public static string UseFilters => I18n.T("filters.useFilters");
        public static string HostsGroup => I18n.T("filters.hostsGroup");
        public static string ShowOnlyTheseHosts => I18n.T("filters.showOnlyTheseHosts");
        public static string HideTheseHosts => I18n.T("filters.hideTheseHosts");
        public static string HostPlaceholder => I18n.T("filters.hostPlaceholder");
        public static string AddHost => I18n.T("filters.addHost");
        public static string RemoveSelectedHost => I18n.T("filters.removeSelectedHost");

        public static string StatusGroup => I18n.T("filters.statusGroup");
        public static string HideSuccess => I18n.T("filters.hideSuccess");
        public static string HideNonSuccess => I18n.T("filters.hideNonSuccess");
        public static string HideRedirects => I18n.T("filters.hideRedirects");
        public static string HideAuthDemands => I18n.T("filters.hideAuthDemands");
        public static string HideNotModified => I18n.T("filters.hideNotModified");

        public static string Actions => I18n.T("filters.actions");
        public static string RunFiltersetNow => I18n.T("filters.runFiltersetNow");
        public static string ShowAllSessions => I18n.T("filters.showAllSessions");
        public static string LoadFilterset => I18n.T("filters.loadFilterset");
        public static string SaveFilterset => I18n.T("filters.saveFilterset");
        public static string HelpMenu => I18n.T("filters.helpMenu");

        public static string LoadCaption => I18n.T("filters.loadCaption");
        public static string SaveCaption => I18n.T("filters.saveCaption");
        public static string FilterSetFilter => I18n.T("filters.filterSetFilter");
        public static string SaveFileName => I18n.T("filters.saveFileName");
        public static string LoadFailed(string message) => I18n.T("filters.loadFailed", ("message", message));
        public static string SaveFailed(string message) => I18n.T("filters.saveFailed", ("message", message));

        public static string HelpCaption => I18n.T("filters.helpCaption");
        public static string HelpBody => I18n.T("filters.helpBody");
    }

    // ---------------------------------------------------------------- autoresponder

    public static class AutoResponder
    {
        /// <summary>Longest preview of a canned body the tester will show.</summary>
        private const int MaxPreviewLength = 90;

        public static string EnableRules => I18n.T("autoResponder.enableRules");
        public static string PassthroughUnmatched => I18n.T("autoResponder.passthroughUnmatched");

        public static string Actions => I18n.T("autoResponder.actions");
        public static string AddRule => I18n.T("autoResponder.addRule");
        public static string EditResponse => I18n.T("autoResponder.editResponse");
        public static string RemoveRule => I18n.T("autoResponder.removeRule");
        public static string MoveUp => I18n.T("autoResponder.moveUp");
        public static string MoveDown => I18n.T("autoResponder.moveDown");
        public static string ResetHitCounts => I18n.T("autoResponder.resetHitCounts");
        public static string ImportRules => I18n.T("autoResponder.importRules");
        public static string ExportRules => I18n.T("autoResponder.exportRules");
        public static string HelpMenu => I18n.T("autoResponder.helpMenu");

        public static string MenuAddRule => I18n.T("autoResponder.menuAddRule");
        public static string MenuEditResponse => I18n.T("autoResponder.menuEditResponse");
        public static string MenuDisableRule => I18n.T("autoResponder.menuDisableRule");
        public static string MenuEnableRule => I18n.T("autoResponder.menuEnableRule");
        public static string MenuMoveUp => I18n.T("autoResponder.menuMoveUp");
        public static string MenuMoveDown => I18n.T("autoResponder.menuMoveDown");
        public static string MenuRemoveRule => I18n.T("autoResponder.menuRemoveRule");

        public static string ColumnOn => I18n.T("autoResponder.columnOn");
        public static string ColumnMatch => I18n.T("autoResponder.columnMatch");
        public static string ColumnAction => I18n.T("autoResponder.columnAction");
        public static string ColumnHits => I18n.T("autoResponder.columnHits");
        public static string ColumnLastMatch => I18n.T("autoResponder.columnLastMatch");

        public static string LabelMatch => I18n.T("autoResponder.labelMatch");
        public static string LabelAction => I18n.T("autoResponder.labelAction");
        public static string LabelBody => I18n.T("autoResponder.labelBody");
        public static string LabelContentType => I18n.T("autoResponder.labelContentType");
        public static string LabelTestUrl => I18n.T("autoResponder.labelTestUrl");
        public static string BrowseButton => I18n.T("autoResponder.browseButton");
        public static string TestButton => I18n.T("autoResponder.testButton");

        public static string NewRuleDescription => I18n.T("autoResponder.newRuleDescription");
        public static string CapturedResponseSaveFailed(string message) =>
            I18n.T("autoResponder.capturedResponseSaveFailed", ("message", message));
        public static string ResponseSaveFailed(string message) =>
            I18n.T("autoResponder.responseSaveFailed", ("message", message));
        public static string EditResponseCaption => I18n.T("autoResponder.editResponseCaption");

        public static string BrowseTooltip => I18n.T("autoResponder.browseTooltip");
        public static string BrowseDisabledTooltip => I18n.T("autoResponder.browseDisabledTooltip");
        public static string TestTooltip => I18n.T("autoResponder.testTooltip");
        public static string TestEmptyTooltip => I18n.T("autoResponder.testEmptyTooltip");
        public static string TestInvalidTooltip => I18n.T("autoResponder.testInvalidTooltip");

        public static string RulesOffPrefix => I18n.T("autoResponder.rulesOffPrefix");
        public static string NoRuleMatches(string prefix) => I18n.T("autoResponder.noRuleMatches", ("prefix", prefix));
        public static string TestDelay(double milliseconds) =>
            I18n.T("autoResponder.testDelay", ("milliseconds", milliseconds));
        public static string TestRespond(string summary, int statusCode, string? reasonPhrase, string preview) =>
            I18n.T("autoResponder.testRespond", ("summary", summary), ("statusCode", statusCode),
                ("reasonPhrase", reasonPhrase), ("preview", preview));
        public static string TestRedirect(string summary, object? target) =>
            I18n.T("autoResponder.testRedirect", ("summary", summary), ("target", target));
        public static string TestKilled(string summary) => I18n.T("autoResponder.testKilled", ("summary", summary));
        public static string TestPassthrough(string summary) =>
            I18n.T("autoResponder.testPassthrough", ("summary", summary));
        public static string PreviewNoBody => I18n.T("autoResponder.previewNoBody");
        public static string PreviewBinary(long bytes) => I18n.T("autoResponder.previewBinary", ("bytes", bytes));

        /// <summary>The cut-off is layout, so it stays here; both wordings are in the catalogue.</summary>
        public static string Preview(string text) => text.Length > MaxPreviewLength
            ? I18n.T("autoResponder.previewTruncated", ("text", text[..MaxPreviewLength]))
            : I18n.T("autoResponder.preview", ("text", text));

        public static string ImportCaption => I18n.T("autoResponder.importCaption");
        public static string ExportCaption => I18n.T("autoResponder.exportCaption");
        public static string RulesFilter => I18n.T("autoResponder.rulesFilter");
        public static string ExportFileName => I18n.T("autoResponder.exportFileName");
        public static string UnreadableRuleSet => I18n.T("autoResponder.unreadableRuleSet");
        public static string ChooseFileCaption => I18n.T("autoResponder.chooseFileCaption");

        public static string HelpCaption => I18n.T("autoResponder.helpCaption");
        public static string HelpBody => I18n.T("autoResponder.helpBody");
    }

    public static class ResponseEditor
    {
        public static string Caption => I18n.T("responseEditor.caption");
        public static string StatusLabel => I18n.T("responseEditor.statusLabel");
        public static string StatusAndHeaders => I18n.T("responseEditor.statusAndHeaders");
        public static string BodyLabel => I18n.T("responseEditor.bodyLabel");
        public static string TabText => I18n.T("responseEditor.tabText");
        public static string TabJson => I18n.T("responseEditor.tabJson");
        public static string PropertyLabel => I18n.T("responseEditor.propertyLabel");
        public static string ValueLabel => I18n.T("responseEditor.valueLabel");

        public static string SelectAProperty => I18n.T("responseEditor.selectAProperty");
        public static string NotJson(string error) => I18n.T("responseEditor.notJson", ("error", error));
        public static string Renamed(string newName) => I18n.T("responseEditor.renamed", ("newName", newName));
        public static string Updated(string label) => I18n.T("responseEditor.updated", ("label", label));
        public static string RootOnlyFromTextTab => I18n.T("responseEditor.rootOnlyFromTextTab");
        public static string IncompleteResponse(string error) =>
            I18n.T("responseEditor.incompleteResponse", ("error", error));
    }

    // ---------------------------------------------------------------- configurations

    /// <summary>The one-time question about anonymous feedback.</summary>
    public static class AnalyticsConsent
    {
        public static string Caption => I18n.T("analyticsConsent.caption");
        public static string Heading => I18n.T("analyticsConsent.heading");
        public static string Intro => I18n.T("analyticsConsent.intro");
        public static string SentHeading => I18n.T("analyticsConsent.sentHeading");
        public static string SentBullets => I18n.T("analyticsConsent.sentBullets");
        public static string NeverHeading => I18n.T("analyticsConsent.neverHeading");
        public static string NeverBullets => I18n.T("analyticsConsent.neverBullets");
        public static string Notes => I18n.T("analyticsConsent.notes");
        public static string ChangeLater => I18n.T("analyticsConsent.changeLater");
        public static string Accept => I18n.T("analyticsConsent.accept");
        public static string Decline => I18n.T("analyticsConsent.decline");
    }

    public static class Configurations
    {
        public static string Caption => I18n.T("configurations.caption");
        public static string PrivacyTab => I18n.T("configurations.privacyTab");
        public static string AnalyticsEnabled => I18n.T("configurations.analyticsEnabled");
        public static string AnalyticsEnabledNote => I18n.T("configurations.analyticsEnabledNote");
        public static string PendingFeedback => I18n.T("configurations.pendingFeedback");
        public static string PendingFeedbackNote => I18n.T("configurations.pendingFeedbackNote");
        public static string OpenReportsFolder => I18n.T("configurations.openReportsFolder");
        public static string GeneralTab => I18n.T("configurations.generalTab");
        public static string HttpsTab => I18n.T("configurations.httpsTab");

        public static string SavedForFutureSessions => I18n.T("configurations.savedForFutureSessions");
        public static string CaptureOnStartup => I18n.T("configurations.captureOnStartup");
        public static string CaptureScopeLabel => I18n.T("configurations.captureScopeLabel");
        public static string ScopeAllProcesses => I18n.T("configurations.scopeAllProcesses");
        public static string ScopeWebBrowsers => I18n.T("configurations.scopeWebBrowsers");
        public static string ScopeNonBrowsers => I18n.T("configurations.scopeNonBrowsers");
        public static string ScopeHideAll => I18n.T("configurations.scopeHideAll");
        public static string CaptureScopeNote => I18n.T("configurations.captureScopeNote");
        public static string WheelZoom => I18n.T("configurations.wheelZoom");
        public static string WheelZoomNote => I18n.T("configurations.wheelZoomNote");

        public static string DecryptHttps => I18n.T("configurations.decryptHttps");
        public static string DecryptHttpsNote => I18n.T("configurations.decryptHttpsNote");
        public static string Http2Downstream => I18n.T("configurations.http2Downstream");
        public static string Http2DownstreamNote => I18n.T("configurations.http2DownstreamNote");
        public static string Http2Upstream => I18n.T("configurations.http2Upstream");
        public static string Http2UpstreamNote => I18n.T("configurations.http2UpstreamNote");
        public static string Http3Upstream => I18n.T("configurations.http3Upstream");
        public static string Http3UpstreamNote => I18n.T("configurations.http3UpstreamNote");
        public static string ValidateUpstream => I18n.T("configurations.validateUpstream");
        public static string ValidateUpstreamNote => I18n.T("configurations.validateUpstreamNote");

        public static string CertificatesGroup => I18n.T("configurations.certificatesGroup");
        public static string TrustRoot => I18n.T("configurations.trustRoot");
        public static string RemoveTrustedRoot => I18n.T("configurations.removeTrustedRoot");
        public static string ExportRoot => I18n.T("configurations.exportRoot");
        public static string OpenCertificateFolder => I18n.T("configurations.openCertificateFolder");
    }

    public static class Hosts
    {
        public static string Caption => I18n.T("hosts.caption");
        public static string EnableRemapping => I18n.T("hosts.enableRemapping");
        public static string Example => I18n.T("hosts.example");
        public static string ImportWindowsHostsFile => I18n.T("hosts.importWindowsHostsFile");
        public static string ImportFailed(string path, string message) =>
            I18n.T("hosts.importFailed", ("path", path), ("message", message));
    }

    // ------------------------------------------------------------------ text wizard

    public static class TextWizard
    {
        public static string Caption => I18n.T("textWizard.caption");
        public static string CaptionWithCounts(int inputLength, int resultLength) =>
            I18n.T("textWizard.captionWithCounts", ("inputLength", inputLength), ("resultLength", resultLength));

        public static string Hint => I18n.T("textWizard.hint");
        public static string TransformLabel => I18n.T("textWizard.transformLabel");
        public static string ViewBytes => I18n.T("textWizard.viewBytes");

        public static string SaveButton => I18n.T("textWizard.saveButton");
        public static string SaveTooltip => I18n.T("textWizard.saveTooltip");
        public static string ToInputButton => I18n.T("textWizard.toInputButton");
        public static string ToInputTooltip => I18n.T("textWizard.toInputTooltip");
        public static string CloseButton => I18n.T("textWizard.closeButton");
        public static string CloseTooltip => I18n.T("textWizard.closeTooltip");

        public static string InputAccessibleName => I18n.T("textWizard.inputAccessibleName");
        public static string OutputAccessibleName => I18n.T("textWizard.outputAccessibleName");
        public static string TransformAccessibleName => I18n.T("textWizard.transformAccessibleName");
        public static string ViewBytesAccessibleName => I18n.T("textWizard.viewBytesAccessibleName");
        public static string StatusAccessibleName => I18n.T("textWizard.statusAccessibleName");

        public static string Truncated(int megabytes) => I18n.T("textWizard.truncated", ("megabytes", megabytes));
        public static string Detected(string transform) => I18n.T("textWizard.detected", ("transform", transform));
        public static string AtLimit(int megabytes) => I18n.T("textWizard.atLimit", ("megabytes", megabytes));
        public static string TransformFailed(string transform, string message) =>
            I18n.T("textWizard.transformFailed", ("transform", transform), ("message", message));
        public static string MoreBytesNotShown(int bytes) => I18n.T("textWizard.moreBytesNotShown", ("bytes", bytes));

        public static string SaveCaption => I18n.T("textWizard.saveCaption");
        public static string SaveFilter => I18n.T("textWizard.saveFilter");
        public static string SaveFileName => I18n.T("textWizard.saveFileName");
        public static string SaveFailed(string message) => I18n.T("textWizard.saveFailed", ("message", message));

        public static string ToBase64 => I18n.T("textWizard.toBase64");
        public static string ToBase64Url => I18n.T("textWizard.toBase64Url");
        public static string FromBase64 => I18n.T("textWizard.fromBase64");
        public static string UrlEncode => I18n.T("textWizard.urlEncode");
        public static string UrlDecode => I18n.T("textWizard.urlDecode");
        public static string HexEncode => I18n.T("textWizard.hexEncode");
        public static string HexDecode => I18n.T("textWizard.hexDecode");
        public static string ToCSharpByteArray => I18n.T("textWizard.toCSharpByteArray");
        public static string ToJsString => I18n.T("textWizard.toJsString");
        public static string FromJsString => I18n.T("textWizard.fromJsString");
        public static string HtmlEncode => I18n.T("textWizard.htmlEncode");
        public static string HtmlDecode => I18n.T("textWizard.htmlDecode");
        public static string ToUtf7 => I18n.T("textWizard.toUtf7");
        public static string FromUtf7 => I18n.T("textWizard.fromUtf7");
        public static string ToDeflatedSaml => I18n.T("textWizard.toDeflatedSaml");
        public static string FromDeflatedSaml => I18n.T("textWizard.fromDeflatedSaml");
        public static string ToMd5 => I18n.T("textWizard.toMd5");
        public static string ToSha1 => I18n.T("textWizard.toSha1");
        public static string ToSha256 => I18n.T("textWizard.toSha256");
        public static string ToSha384 => I18n.T("textWizard.toSha384");
        public static string ToSha512 => I18n.T("textWizard.toSha512");
    }
}
