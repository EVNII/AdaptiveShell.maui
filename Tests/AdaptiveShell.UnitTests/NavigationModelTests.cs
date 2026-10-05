using System.Collections.Specialized;
using AdaptiveShell.Controls;
using NUnit.Framework;

namespace AdaptiveShell.UnitTests;

[TestFixture]
public class NavigationModelTests
{
    static AShellContent Leaf(string title) => new()
    {
        Title = title,
        ContentTemplate = new DataTemplate(() => new ContentPage { Title = title }),
    };

    [Test]
    public void TopLevelMutations_KeepCurrentItemInTheTree()
    {
        var shell = new AShell();
        var home = Leaf("Home");
        var second = Leaf("Second");
        var third = Leaf("Third");

        shell.Items.Add(home);
        Assert.That(shell.CurrentItem, Is.SameAs(home));
        shell.Items.Add(second);
        shell.CurrentItem = second;
        shell.Items.Move(1, 0);
        Assert.That(shell.CurrentItem, Is.SameAs(second));

        shell.Items[0] = third;
        Assert.That(shell.CurrentItem, Is.SameAs(third));
        shell.Items.Remove(third);
        Assert.That(shell.CurrentItem, Is.SameAs(home));
        shell.Items.Clear();
        Assert.That(shell.CurrentItem, Is.Null);
    }

    [Test]
    public void GroupMutations_UpdateFallbackAndPreserveSelectionOnMove()
    {
        var shell = new AShell();
        var group = new AShellGroup { Title = "Media" };
        shell.Items.Add(group);
        Assert.That(shell.CurrentItem, Is.Null);

        var music = Leaf("Music");
        var photos = Leaf("Photos");
        group.Items.Add(music);
        Assert.That(shell.CurrentItem, Is.SameAs(music));
        group.Items.Add(photos);
        shell.CurrentItem = photos;
        group.Items.Move(1, 0);
        Assert.That(shell.CurrentItem, Is.SameAs(photos));

        var video = Leaf("Video");
        group.Items[0] = video;
        Assert.That(shell.CurrentItem, Is.SameAs(video));
        group.Items.Remove(video);
        Assert.That(shell.CurrentItem, Is.SameAs(music));
        group.Items.Clear();
        Assert.That(shell.CurrentItem, Is.Null);
    }

    [Test]
    public void EmptyFirstGroup_DoesNotHideLaterLeaf()
    {
        var shell = new AShell();
        shell.Items.Add(new AShellGroup());
        Assert.That(shell.CurrentItem, Is.Null);

        var home = Leaf("Home");
        shell.Items.Add(home);
        Assert.That(shell.CurrentItem, Is.SameAs(home));
    }

    [Test]
    public void RejectedInsertion_DoesNotChangeEitherCollection()
    {
        var firstShell = new AShell();
        var secondShell = new AShell();
        var home = Leaf("Home");
        firstShell.Items.Add(home);

        Assert.Throws<InvalidOperationException>(() => firstShell.Items.Add(home));
        Assert.Throws<InvalidOperationException>(() => secondShell.Items.Add(home));
        Assert.Throws<InvalidOperationException>(() => firstShell.Items[0] = LeafWithParent(secondShell));
        Assert.Multiple(() =>
        {
            Assert.That(firstShell.Items, Has.Count.EqualTo(1));
            Assert.That(firstShell.Items[0], Is.SameAs(home));
            Assert.That(home.Parent, Is.SameAs(firstShell));
            Assert.That(secondShell.Items, Has.Count.EqualTo(1));
        });
    }

    static AShellContent LeafWithParent(AShell shell)
    {
        var item = Leaf("Owned");
        shell.Items.Add(item);
        return item;
    }

    [Test]
    public void ObservableEvents_SeeFinalLogicalOwnership()
    {
        var shell = new AShell();
        var first = Leaf("First");
        var replacement = Leaf("Replacement");
        shell.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                Assert.That(((AShellItem)e.NewItems![0]!).Parent, Is.SameAs(shell));
            if (e.Action == NotifyCollectionChangedAction.Remove)
                Assert.That(((AShellItem)e.OldItems![0]!).Parent, Is.Null);
            if (e.Action == NotifyCollectionChangedAction.Replace)
            {
                Assert.That(((AShellItem)e.OldItems![0]!).Parent, Is.Null);
                Assert.That(((AShellItem)e.NewItems![0]!).Parent, Is.SameAs(shell));
            }
            if (e.Action == NotifyCollectionChangedAction.Reset)
                Assert.That(first.Parent, Is.Null);
        };

        shell.Items.Add(first);
        shell.Items[0] = replacement;
        shell.Items.Remove(replacement);
        shell.Items.Add(first);
        shell.Items.Clear();
    }

    [Test]
    public void GroupEvents_SeeFinalLogicalOwnership()
    {
        var group = new AShellGroup();
        var first = Leaf("First");
        var replacement = Leaf("Replacement");
        group.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                Assert.That(((AShellContent)e.NewItems![0]!).Parent, Is.SameAs(group));
            if (e.Action == NotifyCollectionChangedAction.Remove)
                Assert.That(((AShellContent)e.OldItems![0]!).Parent, Is.Null);
            if (e.Action == NotifyCollectionChangedAction.Replace)
            {
                Assert.That(((AShellContent)e.OldItems![0]!).Parent, Is.Null);
                Assert.That(((AShellContent)e.NewItems![0]!).Parent, Is.SameAs(group));
            }
            if (e.Action == NotifyCollectionChangedAction.Reset)
                Assert.That(first.Parent, Is.Null);
        };

        group.Items.Add(first);
        group.Items[0] = replacement;
        group.Items.Remove(replacement);
        group.Items.Add(first);
        group.Items.Clear();
    }

    [Test]
    public void CachedPage_DetachesAndReparentsWhenItemMoves()
    {
        var original = new AShell();
        var destination = new AShell();
        var item = Leaf("Page");
        original.Items.Add(item);
        var page = ((IAShellContentController)item).page;
        Assert.That(page.Parent, Is.SameAs(original));

        original.Items.Remove(item);
        Assert.That(page.Parent, Is.Null);
        destination.Items.Add(item);
        Assert.Multiple(() =>
        {
            Assert.That(((IAShellContentController)item).page, Is.SameAs(page));
            Assert.That(page.Parent, Is.SameAs(destination));
        });
    }

    [Test]
    public void MovingWholeGroup_ReparentsExistingChildPage()
    {
        var original = new AShell();
        var destination = new AShell();
        var group = new AShellGroup();
        var child = Leaf("Music");
        group.Items.Add(child);
        original.Items.Add(group);
        var page = ((IAShellContentController)child).page;
        Assert.That(page.Parent, Is.SameAs(original));

        original.Items.Remove(group);
        Assert.That(page.Parent, Is.Null);
        destination.Items.Add(group);
        Assert.Multiple(() =>
        {
            Assert.That(((IAShellContentController)child).page, Is.SameAs(page));
            Assert.That(page.Parent, Is.SameAs(destination));
            Assert.That(group.Items[0], Is.SameAs(child));
        });
    }

    [Test]
    public void RemovingOrReplacingItems_DetachesCachedPages()
    {
        var shell = new AShell();
        var top = Leaf("Top");
        shell.Items.Add(top);
        var topPage = ((IAShellContentController)top).page;
        shell.Items[0] = Leaf("Replacement");
        Assert.That(topPage.Parent, Is.Null);

        var group = new AShellGroup();
        var child = Leaf("Child");
        group.Items.Add(child);
        shell.Items.Add(group);
        var childPage = ((IAShellContentController)child).page;
        group.Items.Clear();
        Assert.That(childPage.Parent, Is.Null);
        shell.Items.Clear();
        Assert.That(shell.CurrentItem, Is.Null);
    }

    [Test]
    public void ChangingTemplate_ReplacesAndReparentsCachedPage()
    {
        var shell = new AShell();
        var item = Leaf("Original");
        shell.Items.Add(item);
        var original = ((IAShellContentController)item).page;

        item.ContentTemplate = new DataTemplate(() => new ContentPage { Title = "New" });
        var replacement = ((IAShellContentController)item).page;
        Assert.Multiple(() =>
        {
            Assert.That(replacement, Is.Not.SameAs(original));
            Assert.That(original.Parent, Is.Null);
            Assert.That(replacement.Parent, Is.SameAs(shell));
            Assert.That(replacement.Title, Is.EqualTo("New"));
        });
    }

    [Test]
    public void TemplateChange_InvalidatesCachedPageBeforeItemsNotification()
    {
        var shell = new AShell();
        var item = Leaf("Original");
        shell.Items.Add(item);
        var original = ((IAShellContentController)item).page;
        var notifications = 0;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AShell.Items)) return;
            notifications++;
            Assert.Multiple(() =>
            {
                Assert.That(item.CachedPage, Is.Not.SameAs(original),
                    "The native Items mapper must not see the old cached page.");
                Assert.That(original.Parent, Is.Null,
                    "The old page must be detached before the native Items mapper runs.");
                if (item.CachedPage is { } replacementDuringNotification)
                    Assert.That(replacementDuringNotification.Title, Is.EqualTo("Replacement"));
            });
        };

        item.ContentTemplate = new DataTemplate(() => new ContentPage { Title = "Replacement" });

        Assert.That(notifications, Is.EqualTo(1));
        var replacement = ((IAShellContentController)item).page;
        Assert.Multiple(() =>
        {
            Assert.That(replacement, Is.Not.SameAs(original));
            Assert.That(replacement.Parent, Is.SameAs(shell));
        });
    }

    [Test]
    public void ExplicitPageReplacement_DetachesOldPageAndAttachesNewPage()
    {
        var shell = new AShell();
        var item = Leaf("Item");
        shell.Items.Add(item);
        var original = ((IAShellContentController)item).page;
        var replacement = new ContentPage { Title = "Replacement" };

        item._page = replacement;

        Assert.Multiple(() =>
        {
            Assert.That(original.Parent, Is.Null);
            Assert.That(((IAShellContentController)item).page, Is.SameAs(replacement));
            Assert.That(replacement.Parent, Is.SameAs(shell));
        });
    }

    [Test]
    public void MetadataChanges_NotifyShellExactlyOnceAndPreserveSelection()
    {
        var shell = new AShell();
        var item = Leaf("Before");
        var group = new AShellGroup();
        var child = Leaf("Child");
        group.Items.Add(child);
        shell.Items.Add(item);
        shell.Items.Add(group);
        var notifications = 0;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AShell.Items)) notifications++;
        };

        item.Title = "After";
        Assert.That(notifications, Is.EqualTo(1));
        item.Icon = "star.svg";
        Assert.That(notifications, Is.EqualTo(2));
        group.Title = "Changed group";
        Assert.That(notifications, Is.EqualTo(3));
        child.Icon = "home.svg";
        Assert.That(notifications, Is.EqualTo(4));
        Assert.That(shell.CurrentItem, Is.SameAs(item));

        shell.CurrentItem = null;
        item.Title = "Again";
        Assert.Multiple(() =>
        {
            Assert.That(notifications, Is.EqualTo(5));
            Assert.That(shell.CurrentItem, Is.Null);
        });

        shell.Items.Remove(group);
        var afterRemoval = notifications;
        group.Title = "Detached group";
        child.Icon = "favorite.svg";
        Assert.That(notifications, Is.EqualTo(afterRemoval),
            "Former group and child must stop notifying the old shell.");

        shell.Items.Remove(item);
        afterRemoval = notifications;
        item.Icon = "folder.svg";
        Assert.That(notifications, Is.EqualTo(afterRemoval));
    }

    [Test]
    public void MovingGroup_TransfersMetadataSubscriptionsToNewShell()
    {
        var original = new AShell();
        var destination = new AShell();
        var group = new AShellGroup();
        var child = Leaf("Music");
        group.Items.Add(child);
        original.Items.Add(group);
        var oldNotifications = 0;
        var newNotifications = 0;
        original.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AShell.Items)) oldNotifications++;
        };
        destination.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AShell.Items)) newNotifications++;
        };

        original.Items.Remove(group);
        destination.Items.Add(group);
        var oldAfterMove = oldNotifications;
        var newAfterMove = newNotifications;
        child.Title = "Updated music";
        Assert.Multiple(() =>
        {
            Assert.That(oldNotifications, Is.EqualTo(oldAfterMove));
            Assert.That(newNotifications, Is.EqualTo(newAfterMove + 1));
        });
    }

    [Test]
    public void DefaultLandingPage_InitializesAccessibleRowAndBindings()
    {
        var group = new AShellGroup { Title = "Media", AutomationId = "media" };
        var child = Leaf("Music");
        child.AutomationId = "music";
        group.Items.Add(child);
        var landing = (ContentPage)DefaultLandingPage.Create(group, _ => { });
        var scroll = (ScrollView)landing.Content;
        var layout = (VerticalStackLayout)scroll.Content;
        var row = (HorizontalStackLayout)layout.Children[0];
        var icon = (Image)row.Children[0];
        var title = (Label)row.Children[1];

        Assert.Multiple(() =>
        {
            Assert.That(scroll.AutomationId, Is.EqualTo("landing-media-body"));
            Assert.That(row.AutomationId, Is.EqualTo("landing-music"));
            Assert.That(landing.Title, Is.EqualTo("Media"));
            Assert.That(title.Text, Is.EqualTo("Music"));
            Assert.That(icon.IsVisible, Is.False);
        });

        Assert.That(landing.IsSet(Page.TitleProperty), Is.True);
        Assert.That(title.IsSet(Label.TextProperty), Is.True);
        Assert.That(icon.IsSet(Image.SourceProperty), Is.True);
        Assert.That(icon.IsSet(VisualElement.IsVisibleProperty), Is.True);

        // A headless MAUI test has no UI dispatcher for asynchronous binding updates.
        // Verify initial values and registered bindings here; native UI tests cover updates.
        var groupWithIcon = new AShellGroup { Title = "Photos" };
        var childWithIcon = Leaf("Gallery");
        childWithIcon.Icon = "star.svg";
        groupWithIcon.Items.Add(childWithIcon);
        var iconPage = (ContentPage)DefaultLandingPage.Create(groupWithIcon, _ => { });
        var iconScroll = (ScrollView)iconPage.Content;
        var iconLayout = (VerticalStackLayout)iconScroll.Content;
        var iconRow = (HorizontalStackLayout)iconLayout.Children[0];
        var visibleIcon = (Image)iconRow.Children[0];
        Assert.Multiple(() =>
        {
            Assert.That(visibleIcon.IsVisible, Is.True);
            Assert.That(visibleIcon.Source, Is.Not.Null);
        });
    }

    [Test]
    public void SettingAutomationIdsAfterLandingCreation_NotifiesShellAndUpdatesRebuiltIds()
    {
        var shell = new AShell();
        var group = new AShellGroup { Title = "Media" };
        var child = Leaf("Music");
        group.Items.Add(child);
        shell.Items.Add(group);
        var firstPage = (ContentPage)DefaultLandingPage.Create(group, _ => { });
        var firstScroll = (ScrollView)firstPage.Content;
        var firstRow = (HorizontalStackLayout)((VerticalStackLayout)firstScroll.Content).Children[0];
        Assert.That(firstScroll.AutomationId, Is.EqualTo("landing-Media-body"));
        Assert.That(firstRow.AutomationId, Is.EqualTo("landing-Music"));

        var notifications = 0;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AShell.Items)) notifications++;
        };
        group.AutomationId = "media";
        child.AutomationId = "music";
        Assert.That(notifications, Is.EqualTo(2));

        var rebuilt = (ContentPage)DefaultLandingPage.Create(group, _ => { });
        var scroll = (ScrollView)rebuilt.Content;
        var row = (HorizontalStackLayout)((VerticalStackLayout)scroll.Content).Children[0];
        Assert.Multiple(() =>
        {
            Assert.That(scroll.AutomationId, Is.EqualTo("landing-media-body"));
            Assert.That(row.AutomationId, Is.EqualTo("landing-music"));
        });
    }
}
