using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MaterialDesignThemes.Wpf;
using RedCompute.Core.Capabilities;

namespace RedCompute.App.ViewModels;

public partial class FriezeLaneViewModel : ObservableObject
{
    public string Slug { get; }
    public string DisplayLabel { get; }
    public PackIconKind IconKind { get; }

    [ObservableProperty]
    private ObservableCollection<FriezeSegment> _segments = new();

    [ObservableProperty]
    private string _durationText = "";

    public FriezeLaneViewModel(string slug, string displayName, CapabilityType type)
    {
        Slug = slug;
        DisplayLabel = slug.Replace("-", " ").ToUpperInvariant();
        IconKind = MapIcon(type);
    }

    internal static PackIconKind MapIcon(CapabilityType type) => type switch
    {
        CapabilityType.Tts => PackIconKind.VolumeHigh,
        CapabilityType.Stt => PackIconKind.Microphone,
        CapabilityType.ImageGen => PackIconKind.Image,
        CapabilityType.MusicGen => PackIconKind.MusicNote,
        CapabilityType.Llm => PackIconKind.Brain,
        CapabilityType.VideoGen => PackIconKind.Video,
        _ => PackIconKind.Cog
    };
}
