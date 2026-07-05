using System.Collections.Generic;
using Newtonsoft.Json;

namespace GitLabMr.Core
{
    // Plain DTOs mapped 1:1 on the GitLab REST v4 JSON payloads.
    // Only the fields the tool actually uses.

    public class GlProject
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("path_with_namespace")] public string PathWithNamespace { get; set; }
        [JsonProperty("default_branch")] public string DefaultBranch { get; set; }
        [JsonProperty("web_url")] public string WebUrl { get; set; }
    }

    public class GlBranch
    {
        [JsonProperty("name")] public string Name { get; set; }
    }

    public class GlMember
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("username")] public string Username { get; set; }
        [JsonProperty("name")] public string Name { get; set; }

        public override string ToString() { return Name + " (@" + Username + ")"; }
    }

    public class GlMergeRequest
    {
        [JsonProperty("iid")] public long Iid { get; set; }
        [JsonProperty("project_id")] public long ProjectId { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("state")] public string State { get; set; }              // opened / merged / closed
        [JsonProperty("web_url")] public string WebUrl { get; set; }
        [JsonProperty("has_conflicts")] public bool HasConflicts { get; set; }
        [JsonProperty("detailed_merge_status")] public string DetailedMergeStatus { get; set; }
        [JsonProperty("source_branch")] public string SourceBranch { get; set; }
        [JsonProperty("target_branch")] public string TargetBranch { get; set; }
        [JsonProperty("draft")] public bool Draft { get; set; }
        [JsonProperty("author")] public GlMember Author { get; set; }
        [JsonProperty("assignees")] public List<GlMember> Assignees { get; set; } = new List<GlMember>();
        [JsonProperty("reviewers")] public List<GlMember> Reviewers { get; set; } = new List<GlMember>();
        [JsonProperty("updated_at")] public System.DateTimeOffset? UpdatedAt { get; set; }
    }

    public class GlErrorBody
    {
        [JsonProperty("message")] public object Message { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
    }

    /// <summary>Everything the "New merge request" web form collects.</summary>
    public class MergeRequestForm
    {
        public string SourceBranch { get; set; }
        public string TargetBranch { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public List<long> AssigneeIds { get; set; } = new List<long>();
        public List<long> ReviewerIds { get; set; } = new List<long>();   // "request review"
        public bool DeleteSourceBranch { get; set; } = true;
        public bool Squash { get; set; }
    }
}
