using Mindweave.Core.Workspaces;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Navigation;

public interface IPageFactory
{
    PageViewModel Create(ContentRoute route, Workspace workspace, IContentNavigator content);
}
