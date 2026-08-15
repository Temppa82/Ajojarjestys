using Microsoft.Win32;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace AjoJarjestys;

public partial class MainWindow : Window
{
 public ObservableCollection<DeliveryStop> Stops { get; }=new();
 DeliveryStop? dragged;
 public MainWindow(){InitializeComponent();StopsGrid.ItemsSource=Stops;UpdateUi();}
 void AddPdf_Click(object s,RoutedEventArgs e){var d=new OpenFileDialog{Filter="PDF-tiedostot (*.pdf)|*.pdf",Multiselect=true};if(d.ShowDialog()==true)ImportFiles(d.FileNames);}
 void Window_DragOver(object s,DragEventArgs e){e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
 void Window_Drop(object s,DragEventArgs e){if(e.Data.GetDataPresent(DataFormats.FileDrop))ImportFiles(((string[])e.Data.GetData(DataFormats.FileDrop)!).Where(x=>Path.GetExtension(x).Equals(".pdf",StringComparison.OrdinalIgnoreCase)));}
 void ImportFiles(IEnumerable<string> paths){
  var before=Stops.Count;
  foreach(var path in paths){if(Stops.Any(x=>x.FilePath.Equals(path,StringComparison.OrdinalIgnoreCase)))continue;try{Stops.Add(PdfAddressExtractor.Extract(path));}catch(Exception ex){MessageBox.Show($"PDF:n lukeminen epäonnistui:\n{path}\n\n{ex.Message}","Virhe");}}
  Renumber();UpdateUi();
  if(Stops.Count>before) Dispatcher.BeginInvoke(new Action(OpenFirstNeedsReview));
}
 void Clear_Click(object s,RoutedEventArgs e){if(Stops.Count==0)return;if(MessageBox.Show("Poistetaanko kaikki lähetteet?","Varmista",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;Stops.Clear();UpdateUi();}
 void AcceptSelected_Click(object s,RoutedEventArgs e){if(StopsGrid.SelectedItem is DeliveryStop x){x.Accepted=true;x.Status="✓ Hyväksytty";StopsGrid.Items.Refresh();UpdateUi();}}
 void EditSelected_Click(object s,RoutedEventArgs e){
  if(StopsGrid.SelectedItem is not DeliveryStop x)return;
  var d=new AddressDialog(x, Stops.Where(z=>!z.Accepted || string.IsNullOrWhiteSpace(z.Address)));
  d.Owner=this;
  d.ShowDialog();
  StopsGrid.Items.Refresh();UpdateUi();
}
void StopsGrid_MouseDoubleClick(object s,MouseButtonEventArgs e){
  if(StopsGrid.SelectedItem is not DeliveryStop x)return;
  var d=new AddressDialog(x, Stops.Where(z=>!z.Accepted || string.IsNullOrWhiteSpace(z.Address))){Owner=this};
  d.ShowDialog(); StopsGrid.Items.Refresh(); UpdateUi();
}
void OpenFirstNeedsReview(){
  var first=Stops.FirstOrDefault(x=>!x.Accepted || string.IsNullOrWhiteSpace(x.Address));
  if(first is null)return;
  var d=new AddressDialog(first, Stops.Where(z=>!z.Accepted || string.IsNullOrWhiteSpace(z.Address))){Owner=this};
  d.ShowDialog(); StopsGrid.Items.Refresh(); UpdateUi();
}
 async void Optimize_Click(object s,RoutedEventArgs e)
 {
  var invalid=Stops.Where(x=>!x.Accepted||string.IsNullOrWhiteSpace(x.Address)).ToList();
  if(invalid.Count>0){MessageBox.Show($"Hyväksy kaikki osoitteet ennen optimointia.\nTarkistettavia: {invalid.Count}","Osoitteiden tarkistus");return;}
  if(Stops.Count==0){MessageBox.Show("Lisää ensin PDF:t.");return;}
  try
  {
   OptimizeButton.IsEnabled=false;RouteSummary.Text="Haetaan lähtöpisteen koordinaatit…";
   var start=await RoutingService.GeocodeAsync(StartAddressBox.Text);
   if(start is null)throw new Exception("Lähtöosoitetta ei löydetty.");
   var points=new List<GeoPoint>();
   var geocodedNow=0;
   foreach(var x in Stops)
   {
    GeoPoint? p=null;
    if(x.Latitude.HasValue && x.Longitude.HasValue)
    {
     p=new GeoPoint(x.Latitude.Value,x.Longitude.Value,x.Address);
    }
    else
    {
     RouteSummary.Text=$"Haetaan osoitetta {points.Count+1}/{Stops.Count}…";
     p=await RoutingService.GeocodeAsync(x.Address);
     if(p is not null)
     {
      x.Latitude=p.Latitude;
      x.Longitude=p.Longitude;
      geocodedNow++;
     }
    }
    if(p is null)throw new Exception($"Osoitetta ei löydetty:\n{x.Address}\n\nKorjaa osoite ja yritä uudelleen.");
    points.Add(p);
   }

   if(geocodedNow>0)
    RouteSummary.Text=$"Uusia osoitteita haettu {geocodedNow}. Välimuistissa olevia osoitteita ei haettu uudelleen.\nLasketaan optimaalista reittiä tieverkolla…";

   GeoPoint? end=null;
   if(!string.IsNullOrWhiteSpace(EndAddressBox.Text))
   {
    RouteSummary.Text="Haetaan päivän pääteosoitteen koordinaatteja…";
    end=await RoutingService.GeocodeAsync(EndAddressBox.Text.Trim());
    if(end is null)throw new Exception($"Pääteosoitetta ei löydetty:\n{EndAddressBox.Text}\n\nTarkista pääteosoite ja yritä uudelleen.");
   }

   RouteSummary.Text= end is null
    ? "Lasketaan optimaalista reittiä tieverkolla…"
    : "Lasketaan optimaalista reittiä tieverkolla lähtöpisteestä pääteosoitteeseen…";
   var result=await RoutingService.OptimizeOpenRouteAsync(start,points,end);
   var old=Stops.ToList();Stops.Clear();foreach(var i in result.OrderedStopIndexes)Stops.Add(old[i]);Renumber();
   RouteSummary.Text=end is null
    ? $"✓ Optimoitu tieverkon mukaan\n{Stops.Count} pysähdystä\n{result.DistanceMeters/1000.0:0.0} km\nArvioitu ajoaika {result.DurationSeconds/60.0:0} min"
    : $"✓ Optimoitu tieverkon mukaan\n{Stops.Count} pysähdystä\nPäättyy: {EndAddressBox.Text.Trim()}\n{result.DistanceMeters/1000.0:0.0} km\nArvioitu ajoaika {result.DurationSeconds/60.0:0} min";
   UpdateUi();
  }
  catch(Exception ex){RouteSummary.Text="Optimointi epäonnistui.";MessageBox.Show(ex.Message,"Reitin optimointi",MessageBoxButton.OK,MessageBoxImage.Warning);}
  finally{OptimizeButton.IsEnabled=true;}
 }
 void SaveOrder_Click(object s,RoutedEventArgs e){var d=new SaveFileDialog{Filter="Tekstitiedosto (*.txt)|*.txt",FileName=$"Ajojärjestys_{DateTime.Now:yyyy-MM-dd}.txt"};if(d.ShowDialog()!=true)return;File.WriteAllLines(d.FileName,Stops.Select((x,i)=>$"{i+1}. {x.Recipient} | {x.Address} | {x.FileName}"));MessageBox.Show("Ajojärjestys tallennettu.");}
 void MergePdf_Click(object s,RoutedEventArgs e)
 {
  if(Stops.Count==0){MessageBox.Show("Lisää ensin PDF:t.");return;}
  if(Stops.Any(x=>!x.Accepted)){MessageBox.Show("Kaikkia osoitteita ei ole hyväksytty.");return;}
  var d=new SaveFileDialog{Filter="PDF-tiedosto (*.pdf)|*.pdf",FileName=$"Ajo_{DateTime.Now:yyyy-MM-dd}.pdf"};if(d.ShowDialog()!=true)return;
  try{using var output=new PdfDocument();foreach(var x in Stops){using var input=PdfReader.Open(x.FilePath,PdfDocumentOpenMode.Import);foreach(var page in input.Pages)output.AddPage(page);}output.Save(d.FileName);MessageBox.Show($"Valmis PDF:\n{d.FileName}","Valmis");}catch(Exception ex){MessageBox.Show(ex.Message,"PDF-virhe");}
 }
 void StopsGrid_MouseMove(object s,MouseEventArgs e){if(e.LeftButton!=MouseButtonState.Pressed)return;if(StopsGrid.SelectedItem is DeliveryStop x){dragged=x;DragDrop.DoDragDrop(StopsGrid,x,DragDropEffects.Move);}}
 void StopsGrid_Drop(object s,DragEventArgs e){if(dragged is null)return;var target=GetRowItem(e.OriginalSource as DependencyObject);if(target is DeliveryStop t&&t!=dragged){Stops.Move(Stops.IndexOf(dragged),Stops.IndexOf(t));Renumber();UpdateUi();}dragged=null;}
 static DeliveryStop? GetRowItem(DependencyObject? source){while(source!=null){if(source is System.Windows.Controls.DataGridRow row)return row.Item as DeliveryStop;source=System.Windows.Media.VisualTreeHelper.GetParent(source);}return null;}
 void Renumber(){for(int i=0;i<Stops.Count;i++)Stops[i].Number=i+1;StopsGrid.Items.Refresh();}
 void UpdateUi(){CountText.Text=$"{Stops.Count} lähetettä";AcceptedText.Text=$"{Stops.Count(x=>x.Accepted)} hyväksytty";ValidationSummary.Text=Stops.Count==0?"Lisää PDF:t aloittaaksesi.":$"{Stops.Count(x=>x.Accepted)} / {Stops.Count} osoitetta hyväksytty.";}
}
