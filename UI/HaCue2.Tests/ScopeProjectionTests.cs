using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.Session;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// F-11 step 2: the scope surface as direct assertions over the document, instead of through the
/// whole shell. The navigator's roots and the tree's scoped rows are pure projections now.
/// </summary>
public sealed class ScopeProjectionTests
{
    [Fact]
    public void ScopesListEveryListAndEveryGroupWithRealTallies()
    {
        var project = ProjectFiles.Create("Show", "/media");
        var list = project.CueLists.First();
        var inner = new GroupCueNode { Number = "2.1", Label = "Inner" };
        inner.Children.Add(new CommentCueNode { Number = "2.1.1", Label = "note" });
        var outer = new GroupCueNode { Number = "2", Label = "Outer" };
        outer.Children.Add(inner);
        list.Cues.Add(outer);

        var scopes = ScopeProjection.Scopes(project);

        var listEntry = scopes.First(scope => scope.IsList && scope.Id == list.Id);
        Assert.Equal(list.Flatten().Count(), listEntry.Count);

        // Tallies count the real subtree, and nesting carries into the navigator's indent.
        var outerEntry = scopes.Single(scope => scope.Id == outer.Id);
        Assert.Equal(2, outerEntry.Count);
        Assert.Equal(0, outerEntry.Depth);
        Assert.Equal(1, scopes.Single(scope => scope.Id == inner.Id).Depth);
    }

    [Fact]
    public void AGroupScopeShowsItsSubtreeAndADeadScopeShowsNothing()
    {
        var project = ProjectFiles.Create("Show", "/media");
        var list = project.CueLists.First();
        var group = new GroupCueNode { Number = "9", Label = "Scoped" };
        group.Children.Add(new CommentCueNode { Number = "9.1", Label = "inside" });
        list.Cues.Add(group);
        var runtime = new ShowRuntime();

        var scopes = ScopeProjection.Scopes(project);
        var rows = ScopeProjection.Rows(scopes.Single(s => s.Id == group.Id), project, runtime);

        Assert.Equal(group.Id, Assert.Single(rows).Id);

        list.Cues.Remove(group);
        Assert.Empty(ScopeProjection.Rows(scopes.Single(s => s.Id == group.Id), project, runtime));
    }

    [Fact]
    public void TheBreadcrumbNamesTheListHoldingAGroup()
    {
        var project = ProjectFiles.Create("Show", "/media");
        var list = project.CueLists.First();
        var group = new GroupCueNode { Number = "3", Label = "Held" };
        list.Cues.Add(group);

        Assert.Equal(list.Name, ScopeProjection.ListNameOf(project, group.Id));
        Assert.Equal("show", ScopeProjection.ListNameOf(project, Guid.NewGuid()));
    }
}
