using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitLabMr.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TeamFoundation.Git.Extensibility;
using Microsoft.Win32;

namespace GitLabMr.Vsix.ToolWindows
{
    /// <summary>Row view-model for the "Open merge requests" list.</summary>
    public sealed class MrListItem
    {
        public GlMergeRequest Mr { get; set; }
        public string Header { get; set; }
        public string BranchInfo { get; set; }
        public string StateInfo { get; set; }
        public string PeopleInfo { get; set; }
        public bool CanMarkReady { get; set; }
        public bool CanApprove { get; set; }
        public bool CanMerge { get; set; }
        public bool IsReadOnly { get; set; }
    }

    public partial class MrToolWindowControl : UserControl
    {
        private ToolSettings _settings;
        private GitContext _gitContext;
        private GlProject _project;
        private GlMergeRequest _currentMr;
        private GlMember _me;

        // Auto-refresh on branch switch: listen to VS's own Git service instead of
        // requiring a manual click of "Refresh context".
        private readonly DispatcherTimer _branchChangeDebounceTimer;
        private IGitExt _gitExt;

        public MrToolWindowControl()
        {
            InitializeComponent();

            // VS's Git service can raise the change notification more than once for a
            // single checkout; debounce so that triggers one refresh instead of several.
            _branchChangeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _branchChangeDebounceTimer.Tick += async (s, e) =>
            {
                _branchChangeDebounceTimer.Stop();
                await RefreshContextAsync();
            };
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsStore.Load();
            txtBaseUrl.Text = _settings.GitLabBaseUrl;
            chkIgnoreTls.IsChecked = _settings.IgnoreTlsErrors;
            lblTokenState.Text = SettingsStore.LoadToken() == null
                ? "No token stored yet."
                : "A token is stored. Leave the field empty to keep it.";

            if (string.IsNullOrEmpty(_settings.GitLabBaseUrl) || SettingsStore.LoadToken() == null)
                expSettings.IsExpanded = true;

            EnsureGitExtSubscription();
            _ = RefreshContextAsync();
        }

        /// <summary>Subscribes to VS's own Git service so branch switches made through the VS
        /// Git UI (or any other means VS's Git provider notices) trigger a refresh.</summary>
        private void EnsureGitExtSubscription()
        {
            if (_gitExt != null) return;
            ThreadHelper.ThrowIfNotOnUIThread();
            _gitExt = Package.GetGlobalService(typeof(IGitExt)) as IGitExt;
            if (_gitExt == null) return;
            _gitExt.PropertyChanged += OnGitExtPropertyChanged;
        }

        private void OnGitExtPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(IGitExt.ActiveRepositories)) return;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                SetStatus("Branch change detected, refreshing...");
                _branchChangeDebounceTimer.Stop();
                _branchChangeDebounceTimer.Start();
            }));
        }

        // =====================================================================
        // Settings
        // =====================================================================

        private void OnSaveSettings(object sender, RoutedEventArgs e)
        {
            _settings.GitLabBaseUrl = txtBaseUrl.Text.Trim();
            _settings.IgnoreTlsErrors = chkIgnoreTls.IsChecked == true;
            SettingsStore.Save(_settings);

            // Only overwrite the stored token if the user typed something.
            if (!string.IsNullOrEmpty(pwdToken.Password))
            {
                SettingsStore.SaveToken(pwdToken.Password);
                pwdToken.Clear();
                lblTokenState.Text = "Token saved (DPAPI-encrypted).";
            }
            SetStatus("Settings saved.");
        }

        private async void OnTestConnection(object sender, RoutedEventArgs e)
        {
            try
            {
                SetStatus("Testing connection...");
                using (var client = CreateClient())
                {
                    _me = await client.GetCurrentUserAsync();
                    SetStatus("Connected as " + _me.Name + " (@" + _me.Username + ").");
                }
            }
            catch (Exception ex)
            {
                SetStatus("Connection failed: " + ex.Message);
            }
        }

        // =====================================================================
        // Repo context + form population
        // =====================================================================

        private void OnRefreshContext(object sender, RoutedEventArgs e)
        {
            _ = RefreshContextAsync();
        }

        private async Task RefreshContextAsync()
        {
            try
            {
                // Solution info must be read on the UI thread.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                string solutionDir = GetSolutionDirectory();
                if (string.IsNullOrEmpty(solutionDir))
                {
                    lblContext.Text = "Open a solution inside a Git repository first.";
                    pnlForm.IsEnabled = false;
                    return;
                }

                string previousBranch = _gitContext?.CurrentBranch;
                SetStatus("Reading git context...");
                _gitContext = await Task.Run(() => GitContextReader.Read(solutionDir));

                // A different branch means the created-MR box (if still showing one the user
                // never approved/merged) belongs to a different piece of work; drop it.
                if (previousBranch != null && previousBranch != _gitContext.CurrentBranch && pnlMr.Visibility == Visibility.Visible)
                {
                    _currentMr = null;
                    pnlMr.Visibility = Visibility.Collapsed;
                }

                lblContext.Text = _gitContext.ProjectPath + "  —  source branch: " + _gitContext.CurrentBranch;

                // Auto-fill base URL from an https remote on first run.
                if (string.IsNullOrEmpty(txtBaseUrl.Text) && !string.IsNullOrEmpty(_gitContext.GuessedBaseUrl))
                    txtBaseUrl.Text = _gitContext.GuessedBaseUrl;

                if (SettingsStore.LoadToken() == null || string.IsNullOrEmpty(CurrentBaseUrl()))
                {
                    SetStatus("Configure the GitLab URL and token, then refresh.");
                    return;
                }

                SetStatus("Loading project data from GitLab...");
                using (var client = CreateClient())
                {
                    _me = await client.GetCurrentUserAsync();
                    _project = await client.GetProjectAsync(_gitContext.ProjectPath);
                    var branches = await client.GetBranchesAsync(_project.Id);
                    var members = await client.GetMembersAsync(_project.Id);

                    cmbTargetBranch.Items.Clear();
                    foreach (var b in branches)
                        if (b.Name != _gitContext.CurrentBranch)
                            cmbTargetBranch.Items.Add(b.Name);
                    cmbTargetBranch.Text = _project.DefaultBranch;

                    FillMemberCombo(cmbAssignee, members);
                    FillMemberCombo(cmbReviewer, members);

                    // Sensible default title = branch name, like the web UI.
                    if (string.IsNullOrEmpty(txtTitle.Text))
                        txtTitle.Text = _gitContext.CurrentBranch.Replace('-', ' ').Replace('_', ' ');
                }

                pnlForm.IsEnabled = true;
                SetStatus("Ready. Source: " + _gitContext.CurrentBranch + " -> target: " + _project.DefaultBranch);

                await LoadMrListAsync();
            }
            catch (Exception ex)
            {
                pnlForm.IsEnabled = false;
                SetStatus("Error: " + ex.Message);
            }
        }

        private static void FillMemberCombo(ComboBox combo, List<GlMember> members)
        {
            combo.Items.Clear();
            combo.Items.Add("(none)");
            foreach (var m in members) combo.Items.Add(m);
            combo.SelectedIndex = 0;
        }

        private string GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var solution = (IVsSolution)Package.GetGlobalService(typeof(SVsSolution));
            if (solution == null) return null;
            solution.GetSolutionInfo(out string dir, out _, out _);
            return dir;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _branchChangeDebounceTimer.Stop();

            if (_gitExt != null)
            {
                _gitExt.PropertyChanged -= OnGitExtPropertyChanged;
                _gitExt = null;
            }
        }

        // =====================================================================
        // Create / clear form
        // =====================================================================

        private async void OnCreateMr(object sender, RoutedEventArgs e)
        {
            if (_project == null || _gitContext == null) return;

            string title = txtTitle.Text.Trim();
            if (string.IsNullOrEmpty(title)) { SetStatus("Title is required."); return; }
            if (chkDraft.IsChecked == true && !title.StartsWith("Draft:", StringComparison.OrdinalIgnoreCase))
                title = "Draft: " + title;

            string target = cmbTargetBranch.Text.Trim();
            if (string.IsNullOrEmpty(target)) { SetStatus("Target branch is required."); return; }
            if (target == _gitContext.CurrentBranch) { SetStatus("Source and target branches are identical."); return; }

            var form = new MergeRequestForm
            {
                SourceBranch = _gitContext.CurrentBranch,
                TargetBranch = target,
                Title = title,
                Description = txtDescription.Text,
                DeleteSourceBranch = chkDeleteSource.IsChecked == true,
                Squash = chkSquash.IsChecked == true
            };
            if (cmbAssignee.SelectedItem is GlMember assignee) form.AssigneeIds.Add(assignee.Id);
            if (cmbReviewer.SelectedItem is GlMember reviewer) form.ReviewerIds.Add(reviewer.Id);

            try
            {
                btnCreate.IsEnabled = false;
                SetStatus("Creating merge request...");
                using (var client = CreateClient())
                {
                    _currentMr = await client.CreateMergeRequestAsync(_project.Id, form);
                }
                ClearFormFields();
                ShowMrPanel();
                SetStatus("Merge request !" + _currentMr.Iid + " created.");
                await LoadMrListAsync();
            }
            catch (Exception ex)
            {
                SetStatus("Create failed: " + ex.Message);
            }
            finally
            {
                btnCreate.IsEnabled = true;
            }
        }

        private void OnClearForm(object sender, RoutedEventArgs e)
        {
            ClearFormFields();
            _currentMr = null;
            pnlMr.Visibility = Visibility.Collapsed;
            SetStatus("Form cleared.");
        }

        /// <summary>Resets the create-MR input fields. Used by the "Clear form" button and,
        /// after a successful create, to ready the form for the next MR without hiding the
        /// just-created MR's panel (title/state + Approve/Merge buttons).</summary>
        private void ClearFormFields()
        {
            txtTitle.Text = string.Empty;
            txtDescription.Text = string.Empty;
            chkDraft.IsChecked = false;
            chkDeleteSource.IsChecked = true;
            chkSquash.IsChecked = false;
            cmbTargetBranch.Text = _project?.DefaultBranch ?? string.Empty;
            if (cmbAssignee.Items.Count > 0) cmbAssignee.SelectedIndex = 0;
            if (cmbReviewer.Items.Count > 0) cmbReviewer.SelectedIndex = 0;
        }

        // =====================================================================
        // Approve / merge (the MR just created from the form)
        // =====================================================================

        private async void OnApprove(object sender, RoutedEventArgs e)
        {
            if (_currentMr == null) return;
            await ApproveMrAsync(_currentMr);
        }

        private async void OnMerge(object sender, RoutedEventArgs e)
        {
            if (_currentMr == null) return;
            if (!ConfirmMerge(_currentMr)) return;
            try
            {
                btnMerge.IsEnabled = false;
                SetStatus("Checking mergeability...");
                using (var client = CreateClient())
                {
                    // GitLab computes mergeability asynchronously; merging too early -> 405.
                    _currentMr = await client.WaitUntilMergeableAsync(_currentMr.ProjectId, _currentMr.Iid);

                    SetStatus("Merging...");
                    _currentMr = await client.MergeAsync(
                        _currentMr.ProjectId, _currentMr.Iid,
                        chkSquash.IsChecked == true,
                        chkDeleteSource.IsChecked == true);
                }
                // Nothing left to do with this MR from here, so close its box
                // instead of leaving stale Approve/Merge buttons on screen.
                string mergedState = _currentMr.State;
                _currentMr = null;
                pnlMr.Visibility = Visibility.Collapsed;
                SetStatus("Merged. State: " + mergedState);
                await LoadMrListAsync();
            }
            catch (GitLabApiException ex)
            {
                // WaitUntilMergeableAsync already puts the blocking status in the message
                // (ci_still_running, discussions_not_resolved, not_approved, conflict, ...).
                SetStatus("Merge blocked: " + ex.Message);
            }
            catch (Exception ex)
            {
                SetStatus("Merge failed: " + ex.Message);
            }
            finally
            {
                btnMerge.IsEnabled = true;
            }
        }

        private async void OnRefreshMr(object sender, RoutedEventArgs e)
        {
            if (_currentMr == null) return;
            try
            {
                using (var client = CreateClient())
                {
                    _currentMr = await client.GetMergeRequestAsync(_currentMr.ProjectId, _currentMr.Iid);
                }
                ShowMrPanel();
                SetStatus("State refreshed.");
            }
            catch (Exception ex)
            {
                SetStatus("Refresh failed: " + ex.Message);
            }
        }

        private void OnOpenMrDefault(object sender, RoutedEventArgs e)
        {
            OpenUrl(_currentMr?.WebUrl, null);
        }

        private void OnOpenMrMenu(object sender, RoutedEventArgs e)
        {
            ShowBrowserMenu((FrameworkElement)sender, _currentMr?.WebUrl);
        }

        // =====================================================================
        // Open MR list
        // =====================================================================

        private async void OnRefreshMrList(object sender, RoutedEventArgs e)
        {
            await LoadMrListAsync();
        }

        private async Task LoadMrListAsync()
        {
            if (_project == null)
            {
                icMrs.ItemsSource = null;
                lblMrListEmpty.Visibility = Visibility.Visible;
                lblMrListEmpty.Text = "Refresh context first (the list needs a resolved GitLab project).";
                return;
            }

            try
            {
                SetStatus("Loading open merge requests...");
                using (var client = CreateClient())
                {
                    if (_me == null) _me = await client.GetCurrentUserAsync();
                    var mrs = await client.GetOpenMergeRequestsAsync(_project.Id);

                    var items = mrs.Select(BuildListItem).ToList();
                    icMrs.ItemsSource = items;
                    lblMrListHeader.Text = "Open merge requests (" + items.Count + ")";
                    lblMrListEmpty.Text = "No open merge requests in this project.";
                    lblMrListEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                SetStatus("Merge request list refreshed.");
            }
            catch (Exception ex)
            {
                SetStatus("Loading the MR list failed: " + ex.Message);
            }
        }

        private MrListItem BuildListItem(GlMergeRequest mr)
        {
            bool isAuthor = _me != null && mr.Author != null && mr.Author.Id == _me.Id;
            bool isReviewer = _me != null && mr.Reviewers != null && mr.Reviewers.Any(r => r.Id == _me.Id);
            bool hasReviewers = mr.Reviewers != null && mr.Reviewers.Count > 0;

            // Reviewers act on the MR. An author with no reviewer assigned may self-validate
            // (the create + approve + merge solo workflow). Anyone else is read-only.
            bool canAct = isReviewer || (isAuthor && !hasReviewers);

            var item = new MrListItem
            {
                Mr = mr,
                Header = "!" + mr.Iid + "  " + mr.Title,
                BranchInfo = mr.SourceBranch + "  →  " + mr.TargetBranch,
                StateInfo = "State: " + mr.State + (mr.Draft ? " (draft)" : "")
                    + "  ·  Merge: " + (mr.DetailedMergeStatus ?? "unknown")
                    + (mr.HasConflicts ? "  ·  CONFLICTS" : "")
                    + (mr.UpdatedAt.HasValue ? "  ·  Updated " + mr.UpdatedAt.Value.ToLocalTime().ToString("g") : ""),
                PeopleInfo = "Author: " + (mr.Author != null ? mr.Author.ToString() : "?")
                    + "  ·  Reviewer: " + (hasReviewers
                        ? string.Join(", ", mr.Reviewers.Select(r => r.ToString()))
                        : "none"),
                CanMarkReady = mr.Draft && (isAuthor || isReviewer),
                CanApprove = canAct && !mr.Draft,
                CanMerge = canAct && !mr.Draft
            };
            item.IsReadOnly = !item.CanMarkReady && !item.CanApprove && !item.CanMerge;
            return item;
        }

        private static MrListItem ItemFrom(object sender)
        {
            // Hyperlink & friends are FrameworkContentElements, not FrameworkElements —
            // handle both so no click handler silently no-ops.
            if (sender is FrameworkElement fe) return fe.DataContext as MrListItem;
            if (sender is FrameworkContentElement fce) return fce.DataContext as MrListItem;
            return null;
        }

        private async void OnMrMarkReady(object sender, RoutedEventArgs e)
        {
            var item = ItemFrom(sender);
            if (item == null) return;
            try
            {
                SetStatus("Marking !" + item.Mr.Iid + " as ready...");
                using (var client = CreateClient())
                {
                    await client.MarkReadyAsync(item.Mr.ProjectId, item.Mr.Iid, item.Mr.Title);
                }
                SetStatus("!" + item.Mr.Iid + " is no longer a draft.");
                await LoadMrListAsync();
            }
            catch (Exception ex)
            {
                SetStatus("Mark ready failed: " + ex.Message);
            }
        }

        private async void OnMrApproveItem(object sender, RoutedEventArgs e)
        {
            var item = ItemFrom(sender);
            if (item == null) return;
            await ApproveMrAsync(item.Mr);
        }

        private async Task ApproveMrAsync(GlMergeRequest mr)
        {
            try
            {
                SetStatus("Approving !" + mr.Iid + "...");
                using (var client = CreateClient())
                {
                    await client.ApproveAsync(mr.ProjectId, mr.Iid);
                }
                SetStatus("!" + mr.Iid + " approved. (If this fails with 401, the project forbids self-approval.)");
            }
            catch (Exception ex)
            {
                SetStatus("Approve failed: " + ex.Message);
            }
        }

        private async void OnMrMergeItem(object sender, RoutedEventArgs e)
        {
            var item = ItemFrom(sender);
            if (item == null) return;
            if (!ConfirmMerge(item.Mr)) return;
            try
            {
                SetStatus("Checking mergeability of !" + item.Mr.Iid + "...");
                using (var client = CreateClient())
                {
                    await client.WaitUntilMergeableAsync(item.Mr.ProjectId, item.Mr.Iid);
                    SetStatus("Merging !" + item.Mr.Iid + "...");
                    // No overrides: respect the squash / delete-source options stored on the MR.
                    await client.MergeAsync(item.Mr.ProjectId, item.Mr.Iid);
                }
                SetStatus("!" + item.Mr.Iid + " merged.");
                await LoadMrListAsync();
            }
            catch (GitLabApiException ex)
            {
                SetStatus("Merge blocked: " + ex.Message);
            }
            catch (Exception ex)
            {
                SetStatus("Merge failed: " + ex.Message);
            }
        }

        private void OnMrOpenDefault(object sender, RoutedEventArgs e)
        {
            OpenUrl(ItemFrom(sender)?.Mr?.WebUrl, null);
        }

        private void OnMrOpenMenu(object sender, RoutedEventArgs e)
        {
            ShowBrowserMenu((FrameworkElement)sender, ItemFrom(sender)?.Mr?.WebUrl);
        }

        // =====================================================================
        // Browser launching
        // =====================================================================

        private void ShowBrowserMenu(FrameworkElement anchor, string url)
        {
            if (string.IsNullOrEmpty(url)) return;

            var menu = new ContextMenu();
            menu.Items.Add(MakeBrowserMenuItem("System default", url, null));
            menu.Items.Add(MakeBrowserMenuItem("Firefox", url, "firefox.exe"));
            menu.Items.Add(MakeBrowserMenuItem("Google Chrome", url, "chrome.exe"));
            menu.PlacementTarget = anchor;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private MenuItem MakeBrowserMenuItem(string label, string url, string browserExe)
        {
            var item = new MenuItem { Header = label };
            if (browserExe != null && FindBrowserExe(browserExe) == null)
            {
                item.IsEnabled = false;
                item.Header = label + " (not found)";
            }
            item.Click += (s, e) => OpenUrl(url, browserExe);
            return item;
        }

        private void OpenUrl(string url, string browserExe)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                if (browserExe == null)
                {
                    // UseShellExecute is mandatory inside devenv: without it net472
                    // tries to run the URL as an executable and throws.
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else
                {
                    string path = FindBrowserExe(browserExe);
                    if (path == null) { SetStatus(browserExe + " was not found on this machine."); return; }
                    Process.Start(new ProcessStartInfo(path, "\"" + url + "\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                SetStatus("Could not open the browser: " + ex.Message);
            }
        }

        /// <summary>Resolve a browser executable via the Windows "App Paths" registry keys.</summary>
        private static string FindBrowserExe(string exeName)
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName))
                {
                    var path = key?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(path))
                    {
                        path = path.Trim('"');
                        if (File.Exists(path)) return path;
                    }
                }
            }
            return null;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>Merging isn't reversible from here, so ask before calling the API.</summary>
        private static bool ConfirmMerge(GlMergeRequest mr)
        {
            return MessageBox.Show(
                "Merge !" + mr.Iid + " \"" + mr.Title + "\" into " + mr.TargetBranch + "?",
                "Confirm merge", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private void ShowMrPanel()
        {
            pnlMr.Visibility = Visibility.Visible;
            lblMrTitle.Text = "!" + _currentMr.Iid + "  " + _currentMr.Title;
            lblMrState.Text = "State: " + _currentMr.State
                + "   Merge status: " + (_currentMr.DetailedMergeStatus ?? "?")
                + (_currentMr.HasConflicts ? "   (CONFLICTS)" : "");
        }

        private GitLabClient CreateClient()
        {
            string url = CurrentBaseUrl();
            string token = SettingsStore.LoadToken();
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("GitLab base URL is not set (see Settings).");
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("No token stored (see Settings).");
            return new GitLabClient(url, token, chkIgnoreTls.IsChecked == true);
        }

        private string CurrentBaseUrl()
        {
            return string.IsNullOrWhiteSpace(txtBaseUrl.Text) ? _settings.GitLabBaseUrl : txtBaseUrl.Text.Trim();
        }

        private void SetStatus(string message)
        {
            lblStatus.Text = message;
        }
    }
}
