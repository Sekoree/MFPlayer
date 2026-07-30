using System.Text.Json;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// "Auto-renumber on insert/reorder" is ON for new cue lists, and it round-trips through the project file.
/// <para>Both halves matter and they pull in opposite directions on load, which is the whole subtlety: a new
/// list must start enabled, while a list that was SAVED with it off must load off. That rules out forcing the
/// value anywhere in the load path - the default has to come from the property initializer, so an explicit
/// <c>false</c> in the file still wins. It also means an existing project keeps whatever it last saved; only
/// lists created from here on start enabled.</para>
/// </summary>
public sealed class AutoRenumberDefaultTests
{
    [Fact]
    public void NewCueList_HasAutoRenumberOn_InTheModelAndTheEditor()
    {
        Assert.True(new CueList().AutoRenumberOnInsert, "a fresh CueList model should auto-renumber");
        Assert.True(
            new CueListEditorViewModel("List 1").AutoRenumberOnInsert,
            "a fresh cue-list editor should auto-renumber");
    }

    [Fact]
    public void NewCueList_CreatedThroughTheCuePlayer_HasItOn()
    {
        // The path an operator actually takes: the initial list, and Add cue list.
        var vm = new CuePlayerViewModel();
        Assert.True(Assert.Single(vm.CueLists).AutoRenumberOnInsert);

        vm.AddCueListCommand.Execute(null);
        Assert.All(vm.CueLists, list => Assert.True(list.AutoRenumberOnInsert));
    }

    [Fact]
    public void TheSetting_RoundTripsThroughTheProjectFile_InBothStates()
    {
        foreach (var enabled in new[] { true, false })
        {
            var list = new CueList { Name = "Cue List 1", AutoRenumberOnInsert = enabled };
            var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
            // Written explicitly in both states - a reader must never have to infer it from a default.
            Assert.Contains("autoRenumberOnInsert", json);

            var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
            Assert.Equal(enabled, loaded.AutoRenumberOnInsert);

            // …and through the view model, which is what the settings dialog edits.
            var vm = CueListEditorViewModel.FromModel(loaded);
            Assert.Equal(enabled, vm.AutoRenumberOnInsert);
            Assert.Equal(enabled, vm.ToModel().AutoRenumberOnInsert);
        }
    }

    [Fact]
    public void AProjectThatSavedItOff_StillLoadsOff_DespiteTheNewDefault()
    {
        // The regression that a "default on" change invites: honouring the initializer must not turn into
        // overriding the file. This is a real saved list (the shape testproject.haplayproj has).
        const string savedOff = """
            {"schema":"HaPlayCueList/v3","name":"Cue List 1","autoRenumberOnInsert":false,"nodes":[]}
            """;
        var loaded = JsonSerializer.Deserialize(savedOff, CueListJsonContext.Default.CueList)!;
        Assert.False(loaded.AutoRenumberOnInsert, "an explicit false in the project file must win");

        // A list from BEFORE the field existed has nothing to say about it, so it takes the new default.
        const string legacy = """{"schema":"HaPlayCueList/v3","name":"Old list","nodes":[]}""";
        var legacyLoaded = JsonSerializer.Deserialize(legacy, CueListJsonContext.Default.CueList)!;
        Assert.True(legacyLoaded.AutoRenumberOnInsert, "a list predating the field should adopt the default");
    }
}
