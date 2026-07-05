using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GitLabMr.Core
{
    /// <summary>What the tool needs to know about the local checkout.</summary>
    public class GitContext
    {
        public string RemoteUrl { get; set; }
        public string CurrentBranch { get; set; }
        /// <summary>e.g. "group/subgroup/project" parsed from the remote URL.</summary>
        public string ProjectPath { get; set; }
        /// <summary>e.g. "https://gitlab.example.com" parsed from the remote URL (empty for ssh remotes).</summary>
        public string GuessedBaseUrl { get; set; }
    }

    /// <summary>
    /// Reads repo context by shelling out to the git CLI ("simple tools" on purpose:
    /// no LibGit2Sharp native binaries to ship inside the VSIX).
    /// </summary>
    public static class GitContextReader
    {
        /// <summary>Throws InvalidOperationException with a readable message on failure.</summary>
        public static GitContext Read(string workingDirectory)
        {
            string branch = RunGit(workingDirectory, "rev-parse --abbrev-ref HEAD");
            string remote = RunGit(workingDirectory, "config --get remote.origin.url");

            var ctx = new GitContext
            {
                CurrentBranch = branch,
                RemoteUrl = remote
            };
            ParseRemote(remote, ctx);
            return ctx;
        }

        /// <summary>
        /// Supported remote shapes:
        ///   https://gitlab.example.com/group/sub/project.git
        ///   https://user@gitlab.example.com/group/project.git
        ///   git@gitlab.example.com:group/sub/project.git
        ///   ssh://git@gitlab.example.com:2222/group/project.git
        /// </summary>
        internal static void ParseRemote(string remote, GitContext ctx)
        {
            if (string.IsNullOrWhiteSpace(remote))
                throw new InvalidOperationException("No 'origin' remote found in this repository.");

            remote = remote.Trim();

            // scp-like ssh syntax: git@host:path
            var scp = Regex.Match(remote, @"^(?:ssh://)?git@(?<host>[^:/]+)(?::\d+)?[:/](?<path>.+?)(?:\.git)?/?$");
            if (scp.Success && !remote.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                ctx.ProjectPath = scp.Groups["path"].Value.Trim('/');
                ctx.GuessedBaseUrl = string.Empty; // can't guess http(s) scheme/port from ssh
                return;
            }

            // http(s)
            var uri = new Uri(remote);
            ctx.ProjectPath = uri.AbsolutePath.Trim('/');
            if (ctx.ProjectPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                ctx.ProjectPath = ctx.ProjectPath.Substring(0, ctx.ProjectPath.Length - 4);
            ctx.GuessedBaseUrl = uri.GetLeftPart(UriPartial.Authority);
        }

        private static string RunGit(string workingDirectory, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var proc = Process.Start(psi))
            {
                string stdout = proc.StandardOutput.ReadToEnd().Trim();
                string stderr = proc.StandardError.ReadToEnd().Trim();
                proc.WaitForExit(10000);

                if (proc.ExitCode != 0)
                    throw new InvalidOperationException("git " + arguments + " failed: " + stderr);
                return stdout;
            }
        }
    }
}
