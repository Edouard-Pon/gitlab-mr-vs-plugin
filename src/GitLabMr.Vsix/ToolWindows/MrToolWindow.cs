using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace GitLabMr.Vsix.ToolWindows
{
    [Guid("a3c5e7f9-1b2d-4e6f-8a9c-0d1e2f3a4b5c")]
    public class MrToolWindow : ToolWindowPane
    {
        public MrToolWindow() : base(null)
        {
            Caption = "GitLab Merge Requests";
            Content = new MrToolWindowControl();
        }
    }
}
