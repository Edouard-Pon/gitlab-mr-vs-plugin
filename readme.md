# GitLab Merge Requests inside Visual Studio

A small Visual Studio 2022/2026 extension that reproduces the GitLab web
"New merge request" flow inside the IDE:

- detects the current repo / branch from the open solution (via the `git` CLI)
- Create an MR: title, draft, description, target branch, assignee, reviewer,
  squash, delete-source-branch
- Approve and Merge the MR
- lists the project's open MRs with state/author/reviewer info, reviewers (or the author of a
  reviewer-less MR) can Mark ready (drafts), Approve and Merge straight from the list
- works with self-hosted GitLab via a Personal Access Token (scope `api`)
- token is stored DPAPI-encrypted per Windows user (`%LOCALAPPDATA%\GitLabMr\token.bin`)

Everything else (commit, push, diff...) is left to your usual tools (Visual Studio Git, Git Extensions, CLI).
