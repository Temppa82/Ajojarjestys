namespace AjoJarjestys;
public class DeliveryStop
{
 public int Number { get; set; }
 public string FilePath { get; set; } = "";
 public string FileName => System.IO.Path.GetFileName(FilePath);
 public string Recipient { get; set; } = "";
 public string Address { get; set; } = "";
 public bool Accepted { get; set; }
 public string Status { get; set; } = "⚠ Tarkista";
 public double? Latitude { get; set; }
 public double? Longitude { get; set; }
 public PdfPreviewInfo? Preview { get; set; }
}
public record GeoPoint(double Latitude, double Longitude, string DisplayName);
public record RouteResult(IReadOnlyList<int> OrderedStopIndexes, double DistanceMeters, double DurationSeconds);

public record PdfCrop(double Left, double Bottom, double Right, double Top);
public record PdfPreviewInfo(int PageNumber, PdfCrop Crop);
