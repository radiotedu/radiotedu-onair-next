namespace OnAirNext.Engine;

public sealed partial class StationEngine
{
    private readonly ProgrammeDisplayState _programmeDisplay = new();
    private string? _publishedProgrammeText;

    private string? ProgrammeDisplayAt(long position)
    {
        if (_programmes is null) return null;
        var at = _anchor.AddSeconds((double)position / AudioFormat.SampleRate);
        return _programmeDisplay.Resolve(_programmes.Snapshot(), _settings.Id, at);
    }

    private void PublishProgrammeMetadata(MediaItem item, long position)
    {
        var text = ProgrammeDisplayAt(position); _publishedProgrammeText = text;
        foreach (var output in _outputs) output.SetNowPlaying(text is null ? item.Artist : "", text ?? item.Title);
    }

    private void RefreshProgrammeMetadata()
    {
        if (_programmes is null || Volatile.Read(ref _audible) is not { } cursor) return;
        var text = ProgrammeDisplayAt(_samples);
        if (text == _publishedProgrammeText) return;
        _publishedProgrammeText = text;
        foreach (var output in _outputs) output.SetNowPlaying(text is null ? cursor.Selection.Item.Artist : "", text ?? cursor.Selection.Item.Title);
    }
}
