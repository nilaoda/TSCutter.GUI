using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using TSCutter.GUI.Models;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class ClipLocalizationRefreshTests
{
    [Fact]
    public void PickedClipRefreshLocalizedTextNotifiesLocalizedDisplayProperties()
    {
        var clip = new PickedClip
        {
            InFileInfo = new FileInfo("sample.ts"),
            StartPosition = 0,
            EndPosition = 188
        };
        var changed = CollectChangedProperties(clip);

        clip.RefreshLocalizedText();

        Assert.Contains(nameof(PickedClip.EstimatedSizeStr), changed);
        Assert.Contains(nameof(PickedClip.StatusText), changed);
    }

    [Fact]
    public void ExportQueueItemRefreshLocalizedTextNotifiesLocalizedDisplayProperties()
    {
        var item = new ExportQueueItem();
        var changed = CollectChangedProperties(item);

        item.RefreshLocalizedText();

        Assert.Contains(nameof(ExportQueueItem.EstimatedSizeStr), changed);
        Assert.Contains(nameof(ExportQueueItem.StatusText), changed);
    }

    [Fact]
    public void ExportQueueItemStatusChangeNotifiesStatusText()
    {
        var item = new ExportQueueItem();
        var changed = CollectChangedProperties(item);

        item.Status = ClipExportStatus.Done;

        Assert.Contains(nameof(ExportQueueItem.Status), changed);
        Assert.Contains(nameof(ExportQueueItem.StatusText), changed);
    }

    private static List<string> CollectChangedProperties(INotifyPropertyChanged source)
    {
        var changed = new List<string>();
        source.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
                changed.Add(args.PropertyName);
        };
        return changed;
    }
}
