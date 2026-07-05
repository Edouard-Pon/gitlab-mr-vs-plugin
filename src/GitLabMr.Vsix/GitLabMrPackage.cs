using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using GitLabMr.Vsix.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace GitLabMr.Vsix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Window = Solution Explorer's GUID, so the tool window docks tabbed next to it by default.
    [ProvideToolWindow(typeof(MrToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    public sealed class GitLabMrPackage : AsyncPackage
    {
        // Must match guidGitLabMrPackage in the .vsct
        public const string PackageGuidString = "b7f0a5d3-4c2e-4c8f-9a1d-2f6e8c3b7a10";

        // Must match guidGitLabMrCmdSet / cmdidShowMrWindow in the .vsct
        public static readonly Guid CommandSetGuid = new Guid("d4a91c2b-7e3f-4b6a-8c5d-1e9f0a2b3c4d");
        public const int CmdIdShowMrWindow = 0x0100;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Menu wiring must happen on the UI thread.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commandService = (OleMenuCommandService)await GetServiceAsync(typeof(IMenuCommandService));
            if (commandService != null)
            {
                var menuCommandId = new CommandID(CommandSetGuid, CmdIdShowMrWindow);
                var menuItem = new MenuCommand(ShowToolWindow, menuCommandId);
                commandService.AddCommand(menuItem);
            }
        }

        private void ShowToolWindow(object sender, EventArgs e)
        {
            // Discard observes the JoinableTask (silences VSTHRD110);
            // JoinableTaskFactory still tracks it for clean shutdown.
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                var window = await ShowToolWindowAsync(typeof(MrToolWindow), 0, create: true, cancellationToken: DisposalToken);
                if (window?.Frame == null)
                    throw new NotSupportedException("Cannot create GitLab MR tool window.");
            });
        }
    }
}
