using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;

namespace AjoJarjestys;

public partial class AddressDialog : Window
{
    private readonly List<DeliveryStop> _queue;
    private int _index;
    public string AddressValue => AddressBox.Text.Trim();
    public string RecipientValue => RecipientBox.Text.Trim();

    public AddressDialog(DeliveryStop stop, IEnumerable<DeliveryStop>? queue = null)
    {
        InitializeComponent();
        _queue = queue?.ToList() ?? new List<DeliveryStop> { stop };
        _index = Math.Max(0, _queue.FindIndex(x => ReferenceEquals(x, stop)));
        if (_index < 0) _index = 0;
        Loaded += async (_, _) => await LoadCurrentAsync();
    }

    private DeliveryStop Current => _queue[_index];

    private async System.Threading.Tasks.Task LoadCurrentAsync()
    {
        FileText.Text = Current.FileName;
        RecipientBox.Text = Current.Recipient;
        AddressBox.Text = Current.Address;
        QueueText.Text = _queue.Count > 1 ? $"Tarkistettava {_index + 1} / {_queue.Count}" : "";
        UpdateButtons();
        PreviewStatus.Visibility = Visibility.Visible;
        PreviewStatus.Text = "Ladataan PDF-esikatselua…";
        try
        {
            var image = await PdfPreviewRenderer.RenderPageAsync(Current.FilePath, Current.Preview?.PageNumber ?? 1);
            var cropped = PdfPreviewRenderer.Crop(image, Current.Preview?.Crop);
            PdfPreview.Source = null;
            PdfPreview.Visibility = Visibility.Collapsed;
            PreviewImage.Source = cropped;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // Never leave the user with an unexplained blank preview.
            try
            {
                await PdfPreview.EnsureCoreWebView2Async();
                PdfPreview.Visibility = Visibility.Visible;
                PdfPreview.Source = new Uri(Path.GetFullPath(Current.FilePath));
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewStatus.Text = "Esikatselun rajaus ei onnistunut. Näytetään koko PDF-sivu." + Environment.NewLine + ex.Message;
                PreviewStatus.Visibility = Visibility.Visible;
            }
            catch (Exception fallbackEx)
            {
                PreviewStatus.Text = "PDF-esikatselua ei voitu avata. Voit silti muokata tietoja alla." + Environment.NewLine + fallbackEx.Message;
                PreviewStatus.Visibility = Visibility.Visible;
            }
        }
    }

    private void UpdateButtons()
    {
        // Buttons are intentionally always available; this keeps the dialog fast for keyboard/mouse use.
    }

    private void SaveCurrentEdits()
    {
        Current.Recipient = RecipientBox.Text.Trim();
        Current.Address = AddressBox.Text.Trim();
        Current.Latitude = null;
        Current.Longitude = null;
        Current.Accepted = !string.IsNullOrWhiteSpace(Current.Address);
        Current.Status = Current.Accepted ? "✓ Hyväksytty" : "⚠ Tarkista";
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_index <= 0) return;
        SaveCurrentEdits();
        _index--;
        await LoadCurrentAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index >= _queue.Count - 1) return;
        SaveCurrentEdits();
        _index++;
        await LoadCurrentAsync();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AddressBox.Text))
        {
            MessageBox.Show("Syötä osoite ennen hyväksymistä.", "Osoitteen tarkistus", MessageBoxButton.OK, MessageBoxImage.Warning);
            AddressBox.Focus();
            return;
        }
        SaveCurrentEdits();
        DialogResult = true;
    }
}

internal static class PdfPreviewRenderer
{
    public static async Task<BitmapSource> RenderPageAsync(string path, int pageNumber)
    {
        return await Task.Run(() =>
        {
            // DocNET uses PDFium internally and is compatible with .NET 8.
            // It also ships the required native x64 PDFium runtime through NuGet.
            using var docReader = Docnet.Core.DocLib.Instance.GetDocReader(
                path,
                new Docnet.Core.Models.PageDimensions(1600, 2263));

            var pageIndex = Math.Clamp(pageNumber - 1, 0, docReader.GetPageCount() - 1);
            using var pageReader = docReader.GetPageReader(pageIndex);

            int width = pageReader.GetPageWidth();
            int height = pageReader.GetPageHeight();
            byte[] pixels = pageReader.GetImage();

            // DocNET returns BGRA pixel data. WPF can consume it directly.
            int stride = width * 4;
            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            bitmap.Freeze();
            return bitmap;
        });
    }

    public static BitmapSource Crop(BitmapSource source, PdfCrop? crop)
    {
        if (crop is null) return source;

        // PdfCrop uses PDF points with origin at the lower-left.
        // The renderer normally produces an A4-like page, but calculate scale from the
        // actual rendered page dimensions so the crop remains usable across PDFs.
        var sx = source.PixelWidth / 595.0;
        var sy = source.PixelHeight / 842.0;
        var left = Math.Clamp((int)Math.Round(crop.Left * sx), 0, Math.Max(0, source.PixelWidth - 1));
        var right = Math.Clamp((int)Math.Round(crop.Right * sx), left + 1, source.PixelWidth);
        var top = Math.Clamp((int)Math.Round((842 - crop.Top) * sy), 0, Math.Max(0, source.PixelHeight - 1));
        var bottom = Math.Clamp((int)Math.Round((842 - crop.Bottom) * sy), top + 1, source.PixelHeight);
        var rect = new Int32Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        return cropped;
    }
}
