namespace PhotoFixer.Api;

// DRAFT COPY of the website's preset table (src/PhotoFixer/Pages/Index.razor),
// kept here so the live site project stays untouched in this private build.
// Before any public release: move this table into PhotoFixer.Core so the
// site and the API share one source of truth.
public sealed record Preset(string Key, string Label, int Width, int Height);

public static class PresetTable
{
    public static readonly IReadOnlyList<Preset> All = new[]
    {
        new Preset("in-passport-print", "India passport photo - paper form print, 35 x 45 mm", 413, 531),
        new Preset("in-passport-online", "India passport photo - online upload, 630 x 810 px", 630, 810),
        new Preset("us-photo-print", "US passport / visa photo - print, 2 x 2 inch", 600, 600),
        new Preset("us-visa-online", "US visa online (DS-160 / DV) - 600 x 600 px", 600, 600),
        new Preset("a4-print", "A4 print - portrait, 210 x 297 mm", 2480, 3508),
        new Preset("a3-print", "A3 print - portrait, 297 x 420 mm", 3508, 4961),
        new Preset("a5-booklet", "A5 booklet page (chosen page size) - portrait, 148 x 210 mm", 1748, 2480),
    };

    public static readonly object PublicList = new
    {
        presets = All,
        notes = new[]
        {
            "A preset fills exact dimensions and switches to fill-crop, like the website. Print presets convert millimetres at 300 DPI; printed size also depends on print settings.",
            "A preset sets dimensions only - it does not make a photo compliant. Framing, background, file type and KB limits follow each form's own rules."
        }
    };

    public static Preset? Find(string key) => All.FirstOrDefault(p => p.Key == key);
}
