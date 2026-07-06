using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace GitLabMr.Core
{
    /// <summary>
    /// Thin HttpClient wrapper over the GitLab REST v4 API.
    /// Mirrors exactly what the web UI does for the MR lifecycle:
    /// create -> (request review) -> approve -> merge.
    /// </summary>
    public sealed class GitLabClient : IDisposable
    {
        private readonly HttpClient _http;

        /// <param name="baseUrl">e.g. https://gitlab.example.com (no trailing /api/v4)</param>
        /// <param name="privateToken">Personal Access Token with 'api' scope</param>
        /// <param name="ignoreTlsErrors">Dev convenience for self-signed certs NOT in the Windows trust store. Prefer trusting the cert properly.</param>
        public GitLabClient(string baseUrl, string privateToken, bool ignoreTlsErrors = false)
        {
            var handler = new HttpClientHandler();
            if (ignoreTlsErrors)
                handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true;

            _http = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/v4/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", privateToken);
        }

        // ---------------------------------------------------------------
        // Lookups used to populate the "New MR" form (like the web UI)
        // ---------------------------------------------------------------

        /// <summary>Resolve "group/subgroup/project" to a project (id, default branch...).</summary>
        public Task<GlProject> GetProjectAsync(string pathWithNamespace)
        {
            return GetAsync<GlProject>("projects/" + Uri.EscapeDataString(pathWithNamespace));
        }

        /// <summary>Branches, for the target-branch dropdown.</summary>
        public Task<List<GlBranch>> GetBranchesAsync(long projectId)
        {
            return GetAsync<List<GlBranch>>("projects/" + projectId + "/repository/branches?per_page=100");
        }

        /// <summary>All members (incl. inherited), for assignee/reviewer dropdowns.</summary>
        public Task<List<GlMember>> GetMembersAsync(long projectId)
        {
            return GetAsync<List<GlMember>>("projects/" + projectId + "/members/all?per_page=100");
        }

        /// <summary>The authenticated user (handy for "assign to me").</summary>
        public Task<GlMember> GetCurrentUserAsync()
        {
            return GetAsync<GlMember>("user");
        }

        // ---------------------------------------------------------------
        // The MR lifecycle
        // ---------------------------------------------------------------

        /// <summary>POST /projects/:id/merge_requests — the whole "New merge request" form in one call.</summary>
        public Task<GlMergeRequest> CreateMergeRequestAsync(long projectId, MergeRequestForm form)
        {
            var body = new Dictionary<string, object>
            {
                ["source_branch"] = form.SourceBranch,
                ["target_branch"] = form.TargetBranch,
                ["title"] = form.Title,
                ["description"] = ToMarkdownHardBreaks(form.Description),
                ["remove_source_branch"] = form.DeleteSourceBranch,
                ["squash"] = form.Squash
            };
            if (form.AssigneeIds.Count > 0) body["assignee_ids"] = form.AssigneeIds;
            if (form.ReviewerIds.Count > 0) body["reviewer_ids"] = form.ReviewerIds;

            return SendAsync<GlMergeRequest>(HttpMethod.Post, "projects/" + projectId + "/merge_requests", body);
        }

        /// <summary>GET a single MR (refresh state / merge status).</summary>
        public Task<GlMergeRequest> GetMergeRequestAsync(long projectId, long iid)
        {
            return GetAsync<GlMergeRequest>("projects/" + projectId + "/merge_requests/" + iid);
        }

        /// <summary>Open MRs of the project, most recently updated first (for the MR list panel).</summary>
        public Task<List<GlMergeRequest>> GetOpenMergeRequestsAsync(long projectId)
        {
            return GetAsync<List<GlMergeRequest>>("projects/" + projectId +
                "/merge_requests?state=opened&order_by=updated_at&sort=desc&per_page=50");
        }

        /// <summary>
        /// "Mark as ready" — removes the draft status. Like the web UI button, GitLab derives
        /// draft-ness from the title, so this rewrites the title without its draft prefix(es).
        /// </summary>
        public Task<GlMergeRequest> MarkReadyAsync(long projectId, long iid, string currentTitle)
        {
            string title = currentTitle ?? string.Empty;
            string stripped;
            while ((stripped = DraftPrefix.Replace(title, string.Empty)) != title)
                title = stripped;

            var body = new Dictionary<string, object> { ["title"] = title };
            return SendAsync<GlMergeRequest>(HttpMethod.Put, "projects/" + projectId + "/merge_requests/" + iid, body);
        }

        private static string ToMarkdownHardBreaks(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\n", "  \n");
        }

        // The draft prefixes GitLab itself recognizes.
        private static readonly System.Text.RegularExpressions.Regex DraftPrefix =
            new System.Text.RegularExpressions.Regex(@"^\s*(?:draft:|\[draft\]|\(draft\)|draft\s*-\s*)\s*",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>PUT reviewer_ids — "request review" after creation.</summary>
        public Task<GlMergeRequest> SetReviewersAsync(long projectId, long iid, List<long> reviewerIds)
        {
            var body = new Dictionary<string, object> { ["reviewer_ids"] = reviewerIds };
            return SendAsync<GlMergeRequest>(HttpMethod.Put, "projects/" + projectId + "/merge_requests/" + iid, body);
        }

        /// <summary>POST .../approve — "validate". May 401/403 if approval rules forbid self-approval.</summary>
        public Task ApproveAsync(long projectId, long iid)
        {
            return SendAsync<object>(HttpMethod.Post, "projects/" + projectId + "/merge_requests/" + iid + "/approve", null);
        }

        /// <summary>
        /// GitLab computes an MR's mergeability asynchronously after creation/approval.
        /// Merging while detailed_merge_status is still "checking"/"unchecked" gets a 405.
        /// Polls until "mergeable" or throws with the blocking status (e.g. ci_still_running,
        /// discussions_not_resolved, draft_status, not_approved, conflict, need_rebase).
        /// </summary>
        public async Task<GlMergeRequest> WaitUntilMergeableAsync(long projectId, long iid,
            int timeoutSeconds = 30, int pollMilliseconds = 1500)
        {
            GlMergeRequest mr = null;
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                mr = await GetMergeRequestAsync(projectId, iid).ConfigureAwait(false);
                string status = mr.DetailedMergeStatus ?? "unchecked";

                if (status == "mergeable")
                    return mr;

                // Still being computed -> keep polling. Anything else is a hard blocker.
                if (status != "checking" && status != "unchecked" && status != "preparing")
                    throw new GitLabApiException(405,
                        "MR !" + iid + " is not mergeable: " + status);

                await Task.Delay(pollMilliseconds).ConfigureAwait(false);
            }

            throw new GitLabApiException(405,
                "MR !" + iid + " mergeability still '" + (mr?.DetailedMergeStatus ?? "?") +
                "' after " + timeoutSeconds + "s.");
        }

        /// <summary>PUT .../merge with no overrides — uses the MR's own squash/remove-source settings.</summary>
        public Task<GlMergeRequest> MergeAsync(long projectId, long iid)
        {
            return SendAsync<GlMergeRequest>(HttpMethod.Put,
                "projects/" + projectId + "/merge_requests/" + iid + "/merge",
                new Dictionary<string, object>());
        }

        /// <summary>PUT .../merge — the green button.</summary>
        public Task<GlMergeRequest> MergeAsync(long projectId, long iid, bool squash, bool removeSourceBranch)
        {
            var body = new Dictionary<string, object>
            {
                ["squash"] = squash,
                ["should_remove_source_branch"] = removeSourceBranch
            };
            return SendAsync<GlMergeRequest>(HttpMethod.Put, "projects/" + projectId + "/merge_requests/" + iid + "/merge", body);
        }

        // ---------------------------------------------------------------
        // Plumbing
        // ---------------------------------------------------------------

        private async Task<T> GetAsync<T>(string relativeUrl)
        {
            var response = await _http.GetAsync(relativeUrl).ConfigureAwait(false);
            return await ReadAsync<T>(response).ConfigureAwait(false);
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string relativeUrl, object body)
        {
            var request = new HttpRequestMessage(method, relativeUrl);
            if (body != null)
                request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request).ConfigureAwait(false);
            return await ReadAsync<T>(response).ConfigureAwait(false);
        }

        private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
        {
            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new GitLabApiException((int)response.StatusCode, ExtractErrorMessage(raw));

            if (string.IsNullOrWhiteSpace(raw)) return default(T);
            return JsonConvert.DeserializeObject<T>(raw);
        }

        private static string ExtractErrorMessage(string raw)
        {
            try
            {
                var err = JsonConvert.DeserializeObject<GlErrorBody>(raw);
                if (err != null)
                {
                    if (err.Message != null) return err.Message.ToString();
                    if (!string.IsNullOrEmpty(err.Error)) return err.Error;
                }
            }
            catch { /* not JSON, fall through */ }
            return raw;
        }

        public void Dispose() { _http.Dispose(); }
    }

    public class GitLabApiException : Exception
    {
        public int StatusCode { get; }

        public GitLabApiException(int statusCode, string message)
            : base("GitLab API error " + statusCode + ": " + message)
        {
            StatusCode = statusCode;
        }
    }
}
