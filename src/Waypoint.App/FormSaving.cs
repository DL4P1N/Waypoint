using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Waypoint.App;

/// <summary>
/// A form's save that may be on its way to a slow server. Done at once, as on this PC's database, the form carries on
/// straight away, the same as before, and a problem is thrown to the click. Otherwise its button says Saving... and
/// can't be pressed again, and the form stays open until the answer comes, so it's saved once and the form can say how
/// it went.
/// </summary>
internal static class FormSaving
{
    public static void Then<T>(Window form, Button button, Task<T> saving, Action<T> done)
    {
        if (saving.IsCompleted)
        {
            done(saving.GetAwaiter().GetResult());
            return;
        }

        Later(form, button, saving, done);
    }

    /// <summary>A problem goes to the window the way one from any click does, and the form stays open.</summary>
    private static async void Later<T>(Window form, Button button, Task<T> saving, Action<T> done)
    {
        var label = button.Content;
        button.IsEnabled = false;
        button.Content = "Saving...";
        CancelEventHandler stay = (_, e) => e.Cancel = true;
        form.Closing += stay;
        T result;
        try
        {
            result = await saving;
        }
        finally
        {
            form.Closing -= stay;
            button.IsEnabled = true;
            button.Content = label;
        }

        done(result);
    }
}
