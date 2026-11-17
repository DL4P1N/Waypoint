using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;

namespace Waypoint.Core.Tests;

public sealed class ThemeTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 2);
    private const double TextContrast = 4.5;
    private const double QuietContrast = 3;
    private const string PrimaryText = "#FFFFFF";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-{Guid.NewGuid():N}.db");
    private readonly WaypointDatabase _database;

    public ThemeTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    public static TheoryData<ThemePalette> Palettes()
    {
        var data = new TheoryData<ThemePalette>();
        foreach (var theme in Themes.All)
        {
            data.Add(theme);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Every_color_used_as_text_is_readable_on_both_backgrounds(ThemePalette palette)
    {
        foreach (var color in new[] { palette.Accent, palette.Urgent, palette.Soon, palette.Money, palette.SecondaryText })
        {
            Assert.True(ThemePalette.Contrast(color, ThemePalette.Background) >= TextContrast, $"{palette.Name} {color} on black");
            Assert.True(ThemePalette.Contrast(color, ThemePalette.Surface) >= TextContrast, $"{palette.Name} {color} on surface");
        }
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Finished_rows_and_quiet_marks_stay_readable(ThemePalette palette)
    {
        foreach (var color in new[] { ThemePalette.FinishedText, ThemePalette.FinishedDetail, palette.QuietMark })
        {
            Assert.True(ThemePalette.Contrast(color, ThemePalette.Background) >= TextContrast, $"{palette.Name} {color} on black");
        }

        foreach (var color in new[] { ThemePalette.FinishedText, ThemePalette.FinishedDetail })
        {
            Assert.True(ThemePalette.Contrast(color, ThemePalette.Surface) >= TextContrast, $"{color} on surface");
            Assert.True(ThemePalette.Contrast(color, ThemePalette.Background) < ThemePalette.Contrast(palette.SecondaryText, ThemePalette.Background), $"{color} is grayer than what's still to do");
        }
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Black_text_on_a_hovered_button_is_readable(ThemePalette palette) =>
        Assert.True(ThemePalette.Contrast(ThemePalette.Background, palette.Accent) >= TextContrast);

    [Fact]
    public void Contrast_matches_the_known_extreme() =>
        Assert.Equal(21, ThemePalette.Contrast("#FFFFFF", "#000000"), 3);

    [Fact]
    public void Gray_is_the_default() =>
        Assert.Equal("gray", _database.GetTheme().Name);

    [Fact]
    public void Typing_a_theme_saves_it()
    {
        var reply = AssistantOps.Apply(new AssistantOp(AssistantOpKind.ThemeSet, "Amber theme"), _database, 0, DateTime.Now);

        Assert.Contains("amber", reply.Text);
        Assert.Equal("amber", _database.GetTheme().Name);
    }

    [Fact]
    public void An_unknown_theme_lists_the_choices_and_changes_nothing()
    {
        _database.SetTheme("teal");

        var reply = _database.SetTheme("sparkly");

        Assert.Contains("mono", reply.Text);
        Assert.Equal("teal", _database.GetTheme().Name);
    }

    [Fact]
    public void A_stale_saved_theme_falls_back_to_the_default()
    {
        _database.SetPreference("theme", "gone");

        Assert.Equal("gray", _database.GetTheme().Name);
    }

    [Fact]
    public void An_exam_within_three_days_is_marked_soon()
    {
        var exam = Item(ItemKind.Exam, Today.AddDays(2));

        Assert.Equal(ItemMark.Soon, ItemMarks.For(exam, Today));
    }

    [Fact]
    public void A_far_off_exam_is_only_schoolwork() =>
        Assert.Equal(ItemMark.School, ItemMarks.For(Item(ItemKind.Exam, Today.AddDays(10)), Today));

    [Fact]
    public void Work_due_today_is_urgent() =>
        Assert.Equal(ItemMark.Urgent, ItemMarks.For(Item(ItemKind.Deadline, Today), Today));

    [Fact]
    public void Past_work_never_turns_red()
    {
        Assert.Equal(ItemMark.School, ItemMarks.For(Item(ItemKind.Deadline, Today.AddDays(-3)), Today));
        Assert.Equal(ItemMark.Personal, ItemMarks.For(Item(ItemKind.Task, Today.AddDays(-3)), Today));
    }

    [Fact]
    public void A_daily_reminder_stays_calm()
    {
        var daily = Item(ItemKind.Reminder, Today) with { Recurs = new Recurrence(RecurrenceUnit.Day) };

        Assert.Equal(ItemMark.Personal, ItemMarks.For(daily, Today));
    }

    [Fact]
    public void Every_colored_mark_explains_itself_and_plain_rows_need_no_tip()
    {
        Assert.Equal("Due today", ItemMarks.Describe(ItemMarks.For(Item(ItemKind.Deadline, Today), Today)));
        Assert.Equal("Exam in the next 3 days", ItemMarks.Describe(ItemMark.Soon));
        Assert.All(new[] { ItemMark.School, ItemMark.Money }, mark => Assert.False(string.IsNullOrEmpty(ItemMarks.Describe(mark))));
        Assert.Null(ItemMarks.Describe(ItemMark.Personal));
    }

    [Fact]
    public void Shopping_gets_the_money_mark() =>
        Assert.Equal(ItemMark.Money, ItemMarks.For(Item(ItemKind.Shopping, null), Today));

    [Fact]
    public void The_palette_stays_the_same_during_the_wind_down()
    {
        var shift = new WorkShift(Today, new TimeOnly(8, 0), new TimeOnly(12, 0), 15);
        var morning = Today.ToDateTime(new TimeOnly(0, 0));
        WorkShift? Shift(DateOnly day) => day == Today ? shift : null;
        var schedule = new BedtimeSchedule(new BedtimeSettings(), null, Shift);
        var plan = schedule.GetPlan(morning.AddHours(-3));

        Assert.NotNull(schedule.GetWindDown(plan.Bedtime.AddMinutes(-30)));
        Assert.Equal(Themes.Default, _database.GetTheme());
        Assert.Null(typeof(ThemePalette).GetMethod("Dimmed"));
        Assert.Null(typeof(BedtimeSchedule).GetMethod("IsDimmed"));
    }

    [Fact]
    public void An_old_dim_at_night_setting_is_ignored()
    {
        var settings = WaypointSettings.Parse("""{"bedtime": {"sleepHours": 7, "dimAtNight": false}}""");
        var choices = BedtimeChoices.Parse("""{"SleepHours": 7.5, "DimAtNight": false}""");

        Assert.Equal(7, settings.Bedtime!.SleepHours);
        Assert.Equal(7.5, choices.SleepHours);
        Assert.Null(typeof(BedtimeSettings).GetProperty("DimAtNight"));
    }

    [Fact]
    public void Less_see_through_is_kept_apart_from_the_theme_and_survives_a_theme_change()
    {
        Assert.False(_database.GetTheme().LessSeeThrough);
        Assert.Equal(Themes.Default.WindowTint, _database.GetTheme().Tint);

        _database.SetLessSeeThrough(true);
        _database.SetTheme("amber");

        var theme = _database.GetTheme();
        Assert.Equal("amber", theme.Name);
        Assert.True(theme.LessSeeThrough);
        Assert.Equal(ThemePalette.SolidWindowTint, theme.Tint);
        Assert.NotEqual(Themes.All.First(palette => palette.Name == "amber"), theme);

        _database.SetLessSeeThrough(false);
        Assert.Equal(Themes.All.First(palette => palette.Name == "amber").WindowTint, _database.GetTheme().Tint);
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Less_see_through_keeps_text_readable_even_over_a_white_window_behind(ThemePalette palette)
    {
        var behind = Over(ThemePalette.SolidWindowTint, "#FFFFFF");
        foreach (var color in new[] { palette.Accent, palette.Urgent, palette.Soon, palette.Money, palette.SecondaryText })
        {
            Assert.True(ThemePalette.Contrast(color, behind) >= TextContrast, $"{palette.Name} {color} over {behind}");
        }
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void The_tint_keeps_text_readable_over_the_acrylic_of_a_white_page(ThemePalette palette)
    {
        var behind = Over(palette.WindowTint, AcrylicOverWhite);
        Assert.True(ThemePalette.Contrast(PrimaryText, behind) >= TextContrast, $"{palette.Name} text over {behind}");
        foreach (var color in ColoredText(palette))
        {
            Assert.True(ThemePalette.Contrast(color, behind) >= QuietContrast, $"{palette.Name} {color} over {behind}");
        }
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void The_tint_keeps_every_color_readable_over_the_acrylic_of_a_black_page(ThemePalette palette)
    {
        var behind = Over(palette.WindowTint, AcrylicOverBlack);
        foreach (var color in BodyText(palette))
        {
            Assert.True(ThemePalette.Contrast(color, behind) >= TextContrast, $"{palette.Name} {color} over {behind}");
        }

        foreach (var color in QuietText(palette))
        {
            Assert.True(ThemePalette.Contrast(color, behind) >= QuietContrast, $"{palette.Name} {color} over {behind}");
        }
    }

    [Fact]
    public void The_tint_is_the_lightest_that_keeps_every_theme_readable_over_white()
    {
        var tint = Themes.Default.WindowTint;
        Assert.All(Themes.All, palette => Assert.Equal(tint, palette.WindowTint));

        var lighter = $"#{Alpha(tint) - 1:X2}{tint[3..]}";
        var behind = Over(lighter, AcrylicOverWhite);
        Assert.Contains(Themes.All, palette =>
            ThemePalette.Contrast(PrimaryText, behind) < TextContrast
                || ColoredText(palette).Any(color => ThemePalette.Contrast(color, behind) < QuietContrast));
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void The_default_tint_keeps_the_blur_and_the_panel_color(ThemePalette palette)
    {
        Assert.Equal(ThemePalette.Surface[1..], palette.WindowTint[3..]);
        Assert.True(Alpha(palette.WindowTint) <= 0x80, $"{palette.Name} stays see-through, at half or less");
        Assert.True(Alpha(ThemePalette.SolidWindowTint) > Alpha(palette.WindowTint), $"Less see-through is more solid than {palette.Name}");
        Assert.Equal(palette.WindowTint, palette.Tint);
        Assert.Equal(ThemePalette.SolidWindowTint, (palette with { LessSeeThrough = true }).Tint);
    }

    /// <summary>Plain text, links, and colored marks, which need 4.5:1.</summary>
    private static string[] BodyText(ThemePalette palette) => [PrimaryText, palette.Accent, palette.Urgent, palette.Soon, palette.Money];

    /// <summary>Secondary and finished text, which need 3:1.</summary>
    private static string[] QuietText(ThemePalette palette) => [palette.SecondaryText, palette.QuietMark, ThemePalette.FinishedText, ThemePalette.FinishedDetail];

    /// <summary>
    /// Links, colored marks, and secondary text. Over a white page they need 3:1, the bar for large text and marks.
    /// Finished rows and the quiet gray are left out there: they're meant to fade, and drop to about 2.2:1.
    /// </summary>
    private static string[] ColoredText(ThemePalette palette) => [palette.Accent, palette.Urgent, palette.Soon, palette.Money, palette.SecondaryText];

    // What the window shows behind its text before the tint, measured from screenshots on Windows 11 26H2. The acrylic
    // blurs what's behind so heavily that a page's text and pictures average out, then lays a middle gray over it
    // (WindowStyling.AcrylicGray), as Windows' own acrylic does. So a plain white page filling the whole window shows
    // as this gray, not white, and is the worst case; a busy, colorful page came out darker, at about #676061. The tint
    // is then blended over it the way WPF draws it, which matched the screenshots to within one step.
    private const string AcrylicOverWhite = "#939393";
    private const string AcrylicOverBlack = "#3F3F3F";

    private static int Alpha(string tint) => Convert.ToInt32(tint.Substring(1, 2), 16);

    /// <summary>A #AARRGGBB tint laid over a #RRGGBB color.</summary>
    private static string Over(string tint, string behind)
    {
        var alpha = Convert.ToInt32(tint.Substring(1, 2), 16) / 255.0;
        int Channel(int index) => (int)Math.Round((Convert.ToInt32(tint.Substring(3 + (index * 2), 2), 16) * alpha)
            + (Convert.ToInt32(behind.Substring(1 + (index * 2), 2), 16) * (1 - alpha)));
        return $"#{Channel(0):X2}{Channel(1):X2}{Channel(2):X2}";
    }

    private static StoredItem Item(ItemKind kind, DateOnly? due) => new(1, kind, "Thing", due, null, false);
}
