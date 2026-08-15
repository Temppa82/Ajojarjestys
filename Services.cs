using System.Globalization;
using System.IO;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace AjoJarjestys;

public static class PdfAddressExtractor
{
    public static DeliveryStop Extract(string path)
    {
        var pages = new List<string>();

        using var pdf = PdfDocument.Open(path);
        foreach (var page in pdf.GetPages())
            pages.Add(page.Text ?? "");

        var text = string.Join("\n", pages);

        // IMPORTANT: use the visual recipient box as the PRIMARY source.
        // PdfPig's page.Text is not guaranteed to preserve the two-column
        // layout of these Pamark PDFs. In particular, a text-only fallback
        // can accidentally join the order metadata or the sender address.
        // The recipient box is anchored by the exact title-case label
        // "Vastaanottaja:" and parsed only from the words physically below it.
        var preview = FindRecipientPreview(pdf);
        var recipient = ExtractRecipientFromPage(pdf);
        var address = ExtractAddressFromPage(pdf);

        // Only if the visual box truly cannot be found do we use the text
        // block parser. Never search the whole document for an address: the
        // sender address must never become a delivery address.
        if (recipient == "Ei tunnistettu")
            recipient = ExtractRecipientFromPageText(pdf);
        if (string.IsNullOrWhiteSpace(address))
            address = ExtractAddressFromPageText(pdf);

        return new DeliveryStop
        {
            FilePath = path,
            Recipient = recipient,
            Address = address,
            Accepted = false,
            Status = string.IsNullOrWhiteSpace(address)
                ? "❌ Ei osoitetta"
                : "⚠ Tarkista",
            Preview = preview
        };
    }

    private static string ExtractRecipientFromPageText(UglyToad.PdfPig.PdfDocument pdf)
    {
        foreach (var page in pdf.GetPages())
        {
            var block = GetRecipientBlock(page.Text ?? "");
            if (string.IsNullOrWhiteSpace(block)) continue;

            var lines = NormalizeLines(block);
            var candidate = ChooseRecipient(lines);
            if (candidate != "Ei tunnistettu") return candidate;

            // Some PDF text layers flatten the whole recipient box into one
            // line. Rebuild logical lines around the postal code/address.
            var looseLines = BuildLooseRecipientLines(block);
            candidate = ChooseRecipient(looseLines);
            if (candidate != "Ei tunnistettu") return candidate;
        }
        return "Ei tunnistettu";
    }

    private static string ExtractAddressFromPageText(UglyToad.PdfPig.PdfDocument pdf)
    {
        foreach (var page in pdf.GetPages())
        {
            var block = GetRecipientBlock(page.Text ?? "");
            if (string.IsNullOrWhiteSpace(block)) continue;

            var address = ExtractAddressFromBlock(block);
            if (!string.IsNullOrWhiteSpace(address)) return address;

            // Fallback for flattened PDF text where line breaks have been lost.
            address = ExtractAddressFromLooseBlock(block);
            if (!string.IsNullOrWhiteSpace(address)) return address;
        }
        return "";
    }

    private static PdfPreviewInfo? FindRecipientPreview(UglyToad.PdfPig.PdfDocument pdf)
    {
        var pageNumber = 0;
        foreach (var page in pdf.GetPages())
        {
            pageNumber++;
            var label = page.GetWords()
                .Where(w => string.Equals(w.Text.Trim(), "Vastaanottaja:", StringComparison.Ordinal))
                .OrderByDescending(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0)
                .FirstOrDefault();
            if (label is null) continue;

            var lx = label.BoundingBox.Left;
            var ly = (label.BoundingBox.Top + label.BoundingBox.Bottom) / 2.0;
            var left = Math.Max(0, lx - 20);
            var right = Math.Min(page.Width, lx + 340);
            var bottom = Math.Max(0, ly - 205);
            var top = Math.Min(page.Height, ly + 42);
            return new PdfPreviewInfo(pageNumber, new PdfCrop(left, bottom, right, top));
        }
        return null;
    }

    private static string ExtractRecipientFromPage(UglyToad.PdfPig.PdfDocument pdf)
    {
        foreach (var page in pdf.GetPages())
        {
            var words = page.GetWords().ToList();
            // There are several visually similar fields named
            // "VASTAANOTTAJA:" later in the form. Do NOT match them
            // case-insensitively. The actual delivery block is the exact
            // title-case "Vastaanottaja:" near the top of the page.
            var labels = words
                .Where(w => string.Equals(w.Text.Trim(), "Vastaanottaja:", StringComparison.Ordinal))
                .OrderByDescending(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0)
                .ToList();
            if (labels.Count == 0) continue;

            // PdfPig uses a bottom-origin coordinate system, so the visually
            // upper label has the LARGEST Y value.
            var label = labels[0];
            var lx = label.BoundingBox.Left;
            var ly = (label.BoundingBox.Top + label.BoundingBox.Bottom) / 2.0;

            // Same visual area as the working address extractor.  Do not use
            // the lower VASTAANOTTAJA field elsewhere on the form.
            var region = words.Where(w =>
            {
                var b = w.BoundingBox;
                var cx = (b.Left + b.Right) / 2.0;
                var cy = (b.Top + b.Bottom) / 2.0;
                return cx >= lx - 20 && cx <= lx + 240 && cy < ly - 2 && cy > ly - 180;
            }).ToList();

            var lines = GroupVisualLines(region);
            if (lines.Count == 0) continue;

            // On these Pamark forms the first visual line immediately below
            // the exact "Vastaanottaja:" label is the recipient name. This is
            // much safer than scoring arbitrary lines: later lines contain
            // the street, postal code, country and occasionally order data.
            foreach (var line in lines)
            {
                var candidate = Regex.Replace(line, @"\s+", " ").Trim();
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (Regex.IsMatch(candidate, @"^(?:Finland|Suomi)$", RegexOptions.IgnoreCase)) continue;
                if (IsPostalLine(candidate)) continue;
                if (IsStreetAddress(candidate)) continue;
                if (Regex.IsMatch(candidate, @"^(?:Lähetyspvm|Toimitusehto|Myyjä|Asiakasnumero|Tilausnumero|Käsittelijä|Keräilijä|Viitteenne|Asiakkaan tilausnumero|Seur\.?nro|Kolleja|Paino|Tilavuus|Yhteyshenkilö|Toim\.?as\.?\s*kontakti|VASTAANOTTAJA|As\.?\s*puh|Pyydetty toimituspvm|Arvioitu toimituspvm|Toimitusohje|Lähettäjä|Tilaaja|Viitenumeronne|Kuljetusliike)\b", RegexOptions.IgnoreCase)) continue;
                return candidate;
            }
        }
        return "Ei tunnistettu";
    }

    private static string ExtractRecipient(string text)
    {
        var lines = NormalizeLines(GetRecipientBlock(text));
        var candidate = ChooseRecipient(lines);
        return string.IsNullOrWhiteSpace(candidate) ? "Ei tunnistettu" : candidate;
    }

    private static string ChooseRecipient(List<string> lines)
    {
        var metadata = new Regex(
            @"^(?:Lähetyspvm|Toimitusehto|Myyjä|Asiakasnumero|Tilausnumero|Käsittelijä|Keräilijä|Viitteenne|Asiakkaan tilausnumero|Seur\.?nro|Kolleja|Paino|Tilavuus|Yhteyshenkilö|Toim\.?as\.?\s*kontakti|VASTAANOTTAJA|As\.?\s*puh|Pyydetty toimituspvm|Arvioitu toimituspvm|Toimitusohje|Lähettäjä|Tilaaja|Viitenumeronne|Kuljetusliike)\b",
            RegexOptions.IgnoreCase);

        var candidates = new List<(string Text, int Score, int Index)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = Regex.Replace(lines[i], @"\s+", " ").Trim(' ', ',', ';', ':');
            line = Regex.Replace(line, @"\s+(?:Toimitusehto|Myyjä|Arvioitu toimituspvm|Pyydetty toimituspvm|Toimitusohje)\s*:?.*$", "", RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(line) || line.Length < 3 || line.Length > 100) continue;
            if (line.Equals("Finland", StringComparison.OrdinalIgnoreCase) || line.Equals("Suomi", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsPostalLine(line)) continue;
            if (metadata.IsMatch(line)) continue;
            if (Regex.IsMatch(line, @"^\+?\d[\d\s()\-]{6,}$")) continue;

            if (TryExtractTrailingOrganisation(line, out var trailingOrg))
                candidates.Add((trailingOrg, 80, i + 10000));

            if (IsStreetAddress(line)) continue;

            var score = 40;
            if (Regex.IsMatch(line, @"\b(?:Oy|Oyj|Ab|Ltd)\.?$", RegexOptions.IgnoreCase)) score += 20;
            if (Regex.IsMatch(line, @"\b(?:Tapiola|Harmaaniitty|Leppävaara|Villa|VTT|Aava|Mehiläinen|Attendo|Luotea)\b", RegexOptions.IgnoreCase)) score += 12;
            if (Regex.IsMatch(line, @"\b(?:Kauppakeskus|Ostoskeskus|Kampus)\b", RegexOptions.IgnoreCase)) score -= 15;
            candidates.Add((line, score, i));
        }

        // Prefer the last meaningful line in the recipient block. This matches
        // layouts such as "Mehiläinen Oy" followed by "Mehiläinen Tapiola"
        // and "Aava ja Pikkujätti Oy" followed by "Aava Tapiola Vastaanotto".
        // A company line containing Oy/Oyj/Ab remains a good fallback.
        var best = candidates
            .OrderByDescending(x => x.Index)
            .ThenByDescending(x => x.Score)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(best.Text)) return best.Text;

        return "Ei tunnistettu";
    }

    private static bool TryExtractTrailingOrganisation(string line, out string organisation)
    {
        organisation = "";
        var m = Regex.Match(line, @"\b\d{1,4}[A-Za-zÅÄÖåäö]?\s*[,.;:]?\s+(?<tail>[A-Za-zÅÄÖåäö][A-Za-zÅÄÖåäö0-9 .&'’\-]{2,})$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var tail = Regex.Replace(m.Groups["tail"].Value, @"\s+", " ").Trim();
        if (!Regex.IsMatch(tail, @"\b(?:Oy|Oyj|Ab|Ltd)\.?$", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(tail, @"\b(?:Toimitusehto|Myyjä|Arvioitu toimituspvm|Toimitusohje)\b", RegexOptions.IgnoreCase)) return false;
        organisation = tail;
        return true;
    }

    private static List<string> BuildLooseRecipientLines(string block)
    {
        var s = Regex.Replace(block.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        s = Regex.Replace(s, @"(?<!\d)(\d{5}\s+[A-Za-zÅÄÖåäöÉéÈèÜü][A-Za-zÅÄÖåäöÉéÈüäö'\-]*(?:\s+[A-Za-zÅÄÖåäöÉéÈèÜü][A-Za-zÅÄÖåäöÉéÈüäö'\-]*)*)", "\n$1\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b([A-Za-zÅÄÖåäöÉéÈüäö][A-Za-zÅÄÖåäöÉéÈüäö0-9.'\-]*(?:\s+[A-Za-zÅÄÖåäöÉéÈüäö0-9.'\-]+){0,2})\s+(\d{1,4}[A-Za-z]?(?:[-/]\d{1,4})?)\b", "\n$1 $2\n", RegexOptions.IgnoreCase);
        return NormalizeLines(s);
    }

    private static string ExtractLooseRecipient(string block)
    {
        var s = Regex.Replace(block.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(s)) return "Ei tunnistettu";
        var address = FindStreetCandidate(s);
        if (string.IsNullOrWhiteSpace(address))
        {
            var postal = Regex.Match(s, @"\b\d{5}\s+[A-Za-zÅÄÖåäöÉéÈèÜü]+\b", RegexOptions.IgnoreCase);
            if (postal.Success) address = FindStreetCandidate(s[(postal.Index + postal.Length)..]);
        }
        if (string.IsNullOrWhiteSpace(address)) return "Ei tunnistettu";
        var am = Regex.Match(s, Regex.Escape(address));
        if (!am.Success) return "Ei tunnistettu";
        var tail = s[(am.Index + am.Length)..].Trim(' ', ',', ';', ':', '-');
        tail = Regex.Replace(tail, @"\b(?:M-kerros|[A-Z]-kerros|kerros|Finland|Suomi)\b", " ", RegexOptions.IgnoreCase);
        tail = Regex.Replace(tail, @"\b(?:Toimitusohje|Lähetyspvm|Lähettäjä|Arvioitu toimituspvm|Pyydetty toimituspvm|Toimitusehto|Myyjä)\b.*$", "", RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(tail)) return "Ei tunnistettu";
        var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0];
        // Prefer the final named recipient after a company line, e.g.
        // "Mehiläinen Oy Mehiläinen Tapiola" -> "Mehiläinen Tapiola".
        for (var take = Math.Min(4, parts.Length); take >= 2; take--)
        {
            var candidate = string.Join(" ", parts[^take..]);
            if (!Regex.IsMatch(candidate, @"\b(?:Oy|Oyj|Ab|Ltd)\.?\b$", RegexOptions.IgnoreCase))
                return candidate.Trim();
        }
        return tail;
    }

    private static string ExtractAddressFromLooseBlock(string block)
    {
        var s = Regex.Replace(block.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(s)) return "";
        var postalMatches = Regex.Matches(s, @"\b(?<postal>\d{5})\s+(?<city>[A-Za-zÅÄÖåäöÉéÈèÜü][A-Za-zÅÄÖåäöÉéÈèÜü'-]*)", RegexOptions.IgnoreCase);
        foreach (Match p in postalMatches)
        {
            var postal = p.Groups["postal"].Value + " " + p.Groups["city"].Value.Trim();
            var after = s[(p.Index + p.Length)..];
            var before = s[..p.Index];
            var candidate = FindStreetCandidate(after);
            if (!string.IsNullOrWhiteSpace(candidate)) return $"{candidate}, {postal}";
            candidate = FindStreetCandidate(before, preferLast: true);
            if (!string.IsNullOrWhiteSpace(candidate)) return $"{candidate}, {postal}";
        }
        return "";
    }

    private static string FindStreetCandidate(string text, bool preferLast = false)
    {
        var clean = Regex.Replace(text, @"\s+", " ").Trim();
        if (clean.Length == 0) return "";
        var tokens = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bad = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "finland", "suomi", "toimitusehto", "toimitusohje", "ateriapalvelu", "ateriap", "luotsiasema", "toimitila", "sivu", "kolleja", "paino" };
        var suffix = new Regex(@"(tie|katu|kuja|polku|ranta|rannantie|väylä|aukio|tori|silta|rinne|mäki|niemi|laakso|portti|raitti|kaari|lähde)$", RegexOptions.IgnoreCase);
        var found = new List<(string Street, int Score, int Index)>();
        for (var i = 1; i < tokens.Length; i++)
        {
            var m = Regex.Match(tokens[i], @"^(\d{1,4}[A-Za-z]?)[,.;:]?$");
            if (!m.Success) continue;
            for (var back = 1; back <= Math.Min(3, i); back++)
            {
                var parts = tokens[(i - back)..i].Select(x => x.Trim(',', '.', ':', ';')).ToArray();
                var street = string.Join(" ", parts);
                if (!Regex.IsMatch(street, @"^[A-Za-zÅÄÖåäöÉéÈèÜü][A-Za-zÅÄÖåäöÉéÈüäö0-9.'\-]*(?:\s+[A-Za-zÅÄÖåäöÉéÈüäö0-9.'\-]+){0,2}$", RegexOptions.IgnoreCase)) continue;
                if (parts.Any(x => bad.Contains(x))) continue;
                var score = back == 1 ? 50 : back == 2 ? 25 : 5;
                if (suffix.IsMatch(street)) score += 35;
                found.Add(($"{street} {m.Groups[1].Value}", score, i));
            }
        }
        if (found.Count == 0) return "";
        return (preferLast ? found.OrderByDescending(x => x.Index).ThenByDescending(x => x.Score) : found.OrderByDescending(x => x.Score).ThenBy(x => x.Index)).First().Street;
    }

    private static string ExtractAddressFromBlock(string block)
    {
        var lines = NormalizeLines(block);
        var postal = lines.FirstOrDefault(IsPostalLine);
        if (string.IsNullOrWhiteSpace(postal)) return "";

        var streets = new List<(string Street, int Index)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (TryExtractStreetAddress(lines[i], out var street))
                streets.Add((street, i));

            // Some PDF text layers split a street address at the house
            // number: "Kutomotie" on one line and "2 Luotea Siivous Oy"
            // on the next. Recombine adjacent lines before giving up.
            if (i > 0 && Regex.IsMatch(lines[i], @"^\d{1,4}[A-Za-zÅÄÖåäö]?[,. ;:]?\b"))
            {
                if (TryExtractStreetAddress($"{lines[i - 1]} {lines[i]}", out var combined))
                    streets.Add((combined, i));
            }
        }

        if (streets.Count == 0) return "";

        var postalIndex = lines.IndexOf(postal);
        var best = streets
            .Select(x => new
            {
                x.Street,
                x.Index,
                Distance = Math.Abs(x.Index - postalIndex),
                Penalty = Regex.IsMatch(lines[x.Index], @"\b(?:Ateriap|Ateriapalvelu|Luotsiasema|Toimitila)\b", RegexOptions.IgnoreCase) ? 30 : 0
            })
            .OrderBy(x => x.Distance + x.Penalty)
            .ThenBy(x => x.Index)
            .First();

        return $"{best.Street}, {postal}";
    }

    private static string ExtractAddress(string text)
    {
        // Text extraction order in the supplied PDF is NOT the same as the
        // visual order. The visible recipient block is:
        //
        // Vastaanottaja:
        // Aava Tapiola Vastaanotto
        // Aava ja Pikkujätti Oy
        // Länsituuli 12
        // Aava Tapiola Vastaanotto
        // 02100 Espoo
        // Finland
        //
        // PdfPig's page.Text returns this block in another order. Therefore
        // address extraction must be based on the PDF word coordinates.
        //
        // The public Extract() method calls this overload only after reading
        // page text, so this method cannot access coordinates. To keep the
        // existing public API intact, the coordinate extractor is invoked
        // from Extract(string path) below.
        return ExtractAddressFromTextFallback(text);
    }

    private static string ExtractAddressFromTextFallback(string text)
    {
        var lines = NormalizeLines(text);

        // Find all postal-code lines and all strict street-address lines.
        // Never simply take the first street in the document: that is the
        // sender's address on this PDF.
        var postal = lines
            .Select((line, index) => new { line, index })
            .Where(x => IsPostalLine(x.line))
            .ToList();

        var streets = lines
            .Select((line, index) =>
            {
                TryExtractStreetAddress(line, out var street);
                return new { line, index, street };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.street))
            .ToList();

        if (postal.Count == 0 || streets.Count == 0)
            return ExtractAddressFromLooseBlock(text);

        // Prefer a postal/street pair occurring close together. For the
        // supplied PDF the recipient pair is:
        //   02100 Espoo
        //   ...
        //   Länsituuli 12
        //
        // while the sender pair is:
        //   01380 Vantaa
        //   Itäinen Valkoisenlähteentie 18
        var pairs = from p in postal
                    from s in streets
                    let distance = Math.Abs(p.index - s.index)
                    where distance <= 6
                    select new { p, s, distance };

        var best = pairs
            .OrderBy(x => x.distance)
            .ThenByDescending(x => x.p.index)
            .FirstOrDefault();

        if (best is null)
            return "";

        return $"{best.s.street}, {best.p.line.Trim()}";
    }

    private static string ExtractAddressFromPage(UglyToad.PdfPig.PdfDocument pdf)
    {
        foreach (var page in pdf.GetPages())
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0)
                continue;

            // Use the upper/visual "Vastaanottaja:" label as the anchor. The
            // lower VASTAANOTTAJA field belongs to the signature/contact area.
            // Anchor strictly to the actual upper recipient label.
            // Do not use RegexOptions.IgnoreCase: the later
            // "VASTAANOTTAJA:" field is not the delivery address.
            var labels = words
                .Where(w => string.Equals(w.Text.Trim(), "Vastaanottaja:", StringComparison.Ordinal))
                .OrderByDescending(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0)
                .ToList();

            if (labels.Count == 0)
                continue;

            var label = labels[0];
            var lx = label.BoundingBox.Left;
            var ly = (label.BoundingBox.Top + label.BoundingBox.Bottom) / 2.0;

            // Do not rely on one fixed column width. Different supplier PDFs
            // place the recipient block at slightly different X positions.
            var region = words.Where(w =>
            {
                var b = w.BoundingBox;
                var cx = (b.Left + b.Right) / 2.0;
                var cy = (b.Top + b.Bottom) / 2.0;
                return cx >= lx - 20 && cx <= lx + 240 && cy < ly - 2 && cy > ly - 180;
            }).ToList();

            var lines = GroupVisualLines(region);
            if (lines.Count == 0)
                continue;

            var postalCandidates = lines
                .Select((text, index) => new { text, index })
                .Where(x => IsPostalLine(x.text))
                .ToList();

            var streetCandidates = lines
                .Select((text, index) =>
                {
                    TryExtractStreetAddress(text, out var street);
                    return new { text, index, street };
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.street))
                .ToList();

            if (postalCandidates.Count == 0 || streetCandidates.Count == 0)
                continue;

            var candidates = new List<(string Address, int Score)>();
            foreach (var p in postalCandidates)
            foreach (var st in streetCandidates)
            {
                var distance = Math.Abs(p.index - st.index);
                if (distance > 8)
                    continue;

                var score = 100 - distance * 8;

                // A recipient block should contain both pieces close together.
                // This strongly beats the sender address elsewhere on the page.
                if (st.index <= p.index) score += 10;
                if (Regex.IsMatch(st.text, @"\b(?:A|B|C|D|E|F|G|H|I|J|K|L|M)-?\s*kerros\b", RegexOptions.IgnoreCase))
                    score += 2;

                // Penalize obvious non-address candidates even if they happen
                // to contain a number.
                if (Regex.IsMatch(st.street, @"\b(?:Ateriap|Ateriapalvelu|Luotsiasema|Toimitila|Seur|Kolleja|Paino)\b", RegexOptions.IgnoreCase))
                    score -= 35;

                candidates.Add(($"{st.street}, {p.text.Trim()}", score));
            }

            var best = candidates.OrderByDescending(x => x.Score).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(best.Address))
                return best.Address;
        }

        return "";
    }

    private static List<string> GroupVisualLines(List<UglyToad.PdfPig.Content.Word> words)
    {
        var groups = new List<(double Y, List<UglyToad.PdfPig.Content.Word> Words)>();

        foreach (var word in words.OrderByDescending(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0))
        {
            var cy = (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2.0;
            var index = groups.FindIndex(g => Math.Abs(g.Y - cy) <= 3.5);
            if (index >= 0)
                groups[index].Words.Add(word);
            else
                groups.Add((cy, new List<UglyToad.PdfPig.Content.Word> { word }));
        }

        return groups
            .OrderByDescending(g => g.Y)
            .Select(g => string.Join(" ", g.Words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text.Trim())))
            .Select(x => Regex.Replace(x, @"\s+", " ").Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }

    private static string GetRecipientBlock(string text)
    {
        var normalized = text
            .Replace('\u00A0', ' ')
            .Replace('\u2007', ' ')
            .Replace('\u202F', ' ');

        // There are TWO visually different fields in these delivery notes:
        //   "Vastaanottaja:"  = the actual delivery address block
        //   "VASTAANOTTAJA:"  = a phone/signature field later on the form
        // A case-insensitive search treats them as the same label. That was
        // the root cause of the sender address being selected as recipient.
        // Prefer the exact title-case label and score all occurrences instead
        // of blindly taking the first textual match.
        var matches = Regex.Matches(normalized, @"Vastaanottaja\s*:", RegexOptions.None);
        if (matches.Count == 0)
        {
            // Fallback for PDF text layers that upper-case everything, but
            // explicitly reject the lower form field when it is followed by a
            // phone number.
            matches = Regex.Matches(
                normalized,
                @"VASTAANOTTAJA\s*:(?!\s*\+?\d)",
                RegexOptions.None);
        }

        string bestBlock = "";
        var bestScore = int.MinValue;

        foreach (Match match in matches)
        {
            var remainder = normalized[(match.Index + match.Length)..];

            // The recipient box ends at Toimitusohje on these Pamark forms.
            // Do not let the following order metadata or sender section leak
            // into the candidate block.
            var stop = Regex.Match(
                remainder,
                @"\b(?:TOIMITUSOHJE|LÄHETYSPVM|LÄHETTÄJÄ|ASIAKASNUMERO|TOIM\.?\s*AS\.?\s*KONTAKTI|KOLLEJA|SEUR\.?\s*NRO|VIITTEENNE|ASIAKKAAN\s+TILAUSNUMERO|TILAAJA|TOIMITUSEHTO|VIITENUMERONNE|KULJETUSLIIKE)\b",
                RegexOptions.IgnoreCase);

            if (stop.Success && stop.Index > 0)
                remainder = remainder[..stop.Index];

            if (remainder.Length > 3000)
                remainder = remainder[..3000];

            var score = 0;
            if (Regex.IsMatch(remainder, @"\b\d{5}\s+[A-Za-zÅÄÖåäöÉéÈèÜü][A-Za-zÅÄÖåäöÉéÈèÜü'\-]*\b"))
                score += 100;
            if (Regex.IsMatch(remainder, @"\b[A-Za-zÅÄÖåäöÉéÈèÜü][A-Za-zÅÄÖåäöÉéÈèÜü0-9.'\-]*\s+\d{1,4}[A-Za-z]?\b"))
                score += 100;
            if (Regex.IsMatch(remainder, @"\b(?:Finland|Suomi)\b", RegexOptions.IgnoreCase))
                score += 10;
            if (Regex.IsMatch(remainder, @"\b(?:Keräilijä|Käsittelijä|Tilausnumero|Myyjä|Arvioitu toimituspvm)\b", RegexOptions.IgnoreCase))
                score -= 100;

            // Exact title-case "Vastaanottaja:" is the preferred anchor.
            if (match.Value == "Vastaanottaja:")
                score += 50;

            if (score > bestScore)
            {
                bestScore = score;
                bestBlock = remainder;
            }
        }

        return bestBlock;
    }

    private static string NormalizeBlock(string value)
    {
        // Convert line breaks/tabs to spaces. The extraction must not depend
        // on where PdfPig happens to place a visual line break.
        return Regex.Replace(
                value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '),
                @"\s+",
                " ")
            .Trim();
    }

    private static List<string> NormalizeLines(string value)
    {
        return value
            .Replace('\r', '\n')
            .Replace('\t', ' ')
            .Split('\n')
            .Select(x => Regex.Replace(x.Trim(), @"\s+", " "))
            .Where(x => x.Length > 0)
            .ToList();
    }

    private static bool IsPostalLine(string value)
    {
        return Regex.IsMatch(
            value,
            @"^\d{5}\s+[A-Za-zÅÄÖåäöÉéÈèÜü][A-Za-zÅÄÖåäöÉéÈèÜü'\-]*(?:\s+[A-Za-zÅÄÖåäöÉéÈèÜü][A-Za-zÅÄÖåäöÉéÈèÜü'\-]*)*$",
            RegexOptions.IgnoreCase);
    }

    private static bool IsStreetAddress(string value)
    {
        return TryExtractStreetAddress(value, out _);
    }

    private static bool TryExtractStreetAddress(string value, out string address)
    {
        address = "";
        var line = CleanStreetCandidate(value);
        if (string.IsNullOrWhiteSpace(line))
            return false;

        // A delivery note may put the street, recipient and company on the
        // SAME visual/text line. Examples seen in the user's PDFs include:
        //   "Kutomotie 2 Luotea Siivous Oy"
        //   "Management Maarinrannantie Oy 4"
        //   "Caverion Espoo Viitikka 4"
        //   "Toimitila 9863"
        //
        // The old regex greedily consumed up to four words before the house
        // number. That is precisely what caused company names to become part
        // of the address. Instead, inspect every house-number candidate and
        // score the words immediately before it.

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return false;

        var companyWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "oy", "oyj", "ab", "ltd", "inc", "corp", "co",
            "caverion", "management", "attendo", "aava",
            "kauppakeskus", "vastaanotto", "toimitila", "luotsiasema",
            "siivous", "palvelut", "business", "finland", "suomi"
        };

        // Typical Finnish street-name endings. This is a scoring signal, not
        // a hard requirement, because names such as "Viitikka" and
        // "Länsituuli" do not necessarily have a suffix.
        var streetSuffix = new Regex(
            @"(?:tie|katu|kuja|polku|ranta|rannantie|väylä|aukio|tori|silta|rinne|mäki|niemi|laakso|kuja|portti|raitti|kaari|väylä|lähde)$",
            RegexOptions.IgnoreCase);

        var candidates = new List<(string Street, string Number, int Score)>();

        for (var numberIndex = 1; numberIndex < tokens.Length; numberIndex++)
        {
            var numberMatch = Regex.Match(
                tokens[numberIndex],
                @"^(?<number>\d{1,4})(?<suffix>[A-Za-zÅÄÖåäö]?)[,.;:]?$");

            if (!numberMatch.Success)
                continue;

            var number = numberMatch.Groups["number"].Value;
            if (number.Length == 4 && number.StartsWith("0"))
                continue;

            // Prefer the closest word before the house number. Normally that
            // alone is the street name. If it is a company suffix such as
            // "Oy", look one or two words further back.
            // Special but common layout: the company suffix is printed
            // immediately before the house number, e.g.
            // "Management Maarinrannantie Oy 4". In that case the actual
            // street name is the token before "Oy".
            var immediateToken = tokens[numberIndex - 1].Trim(',', '.', ':', ';');
            if (companyWords.Contains(immediateToken) && numberIndex >= 2)
            {
                var streetToken = tokens[numberIndex - 2].Trim(',', '.', ':', ';');
                if (!companyWords.Contains(streetToken) &&
                    Regex.IsMatch(streetToken, @"^[A-Za-zÅÄÖåäöÉéÈèÜü][A-Za-zÅÄÖåäöÉéÈüäö0-9.'\-]*$"))
                {
                    var score = 38;
                    if (streetSuffix.IsMatch(streetToken))
                        score += 35;
                    candidates.Add((streetToken, number, score));
                }
            }

            for (var back = 1; back <= Math.Min(4, numberIndex); back++)
            {
                var first = numberIndex - back;
                var streetTokens = tokens.Skip(first).Take(back).ToArray();
                var street = string.Join(" ", streetTokens).Trim();

                if (street.Length < 2)
                    continue;

                // Do not allow company/recipient words into the candidate.
                if (streetTokens.Any(t => companyWords.Contains(t.Trim(',', '.', ':', ';'))))
                    continue;

                // A candidate must start with a letter and contain letters.
                if (!Regex.IsMatch(street, @"^[A-Za-zÅÄÖåäöÉéÈèÜü]"))
                    continue;

                var score = 0;

                // Short candidates are safer: "Viitikka" beats
                // "Caverion Espoo Viitikka".
                score += back == 1 ? 40 : back == 2 ? 20 : 5;

                // Strong signal for real street names.
                if (streetSuffix.IsMatch(street))
                    score += 35;

                // A street name containing a digit is unusual but valid in
                // some addresses; don't reject it outright.
                if (Regex.IsMatch(street, @"\d"))
                    score -= 5;

                // Reject obvious non-address labels.
                if (Regex.IsMatch(street,
                    @"^(?:Finland|Suomi|Sivu|Seur|Kolleja|Paino|Toimitila|Luotsiasema|Vastaanottaja|Vastaanotto)$",
                    RegexOptions.IgnoreCase))
                    continue;

                // If the first token looks like a company/brand, do not let a
                // longer candidate swallow it. This catches e.g. Caverion
                // Espoo Viitikka 4 while still allowing a genuine two-word
                // street such as "Itäinen Valkoisenlähteentie 18".
                if (back > 1 && companyWords.Contains(streetTokens[0]))
                    continue;

                candidates.Add((street, number, score));
            }
        }

        var best = candidates
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Street.Length)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(best.Street))
            return false;

        address = $"{best.Street} {best.Number}";
        return true;
    }

    private static string CleanStreetCandidate(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ").Trim(' ', ',', ';', ':');
    }
}

public static class RoutingService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTime LastNominatimRequestUtc = DateTime.MinValue;

    // v3.5: successful geocoding results are cached locally. This is the
    // important part: pressing OPTIMOI repeatedly must NOT send the same
    // address to Nominatim again and again.
    private static readonly object CacheLock = new();
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AjoJarjestys");
    private static readonly string CacheFile = Path.Combine(CacheDirectory, "geocode-cache.json");
    private static Dictionary<string, GeoPoint> GeocodeCache = LoadCache();

    // Public Nominatim has a maximum of 1 request/second and explicitly asks
    // applications to cache results. We therefore serialize requests and
    // keep a small margin above one second.
    private static async Task WaitForNominatimSlotAsync(CancellationToken ct)
    {
        await RequestGate.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - LastNominatimRequestUtc;
            var wait = TimeSpan.FromMilliseconds(1200) - elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);

            LastNominatimRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AjoJarjestys/3.6.7 (Windows desktop delivery route planner)");
        c.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return c;
    }

    private static string NormalizeCacheKey(string address)
    {
        return Regex.Replace(address.Trim().ToLowerInvariant(), @"\s+", " ");
    }

    private static Dictionary<string, GeoPoint> LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile))
                return new Dictionary<string, GeoPoint>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(CacheFile);
            var data = JsonSerializer.Deserialize<Dictionary<string, GeoPoint>>(json);
            return data is null
                ? new Dictionary<string, GeoPoint>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GeoPoint>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // A damaged cache must never prevent the application from starting.
            return new Dictionary<string, GeoPoint>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            Dictionary<string, GeoPoint> snapshot;
            lock (CacheLock)
                snapshot = new Dictionary<string, GeoPoint>(GeocodeCache, StringComparer.OrdinalIgnoreCase);

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var temp = CacheFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, CacheFile, true);
        }
        catch
        {
            // Cache is an optimization only. A write failure must not break routing.
        }
    }

    private static bool TryGetCached(string address, out GeoPoint point)
    {
        var key = NormalizeCacheKey(address);
        lock (CacheLock)
            return GeocodeCache.TryGetValue(key, out point!);
    }

    private static void PutCached(string address, GeoPoint point)
    {
        var key = NormalizeCacheKey(address);
        lock (CacheLock)
            GeocodeCache[key] = point;
        SaveCache();
    }

    public static async Task<GeoPoint?> GeocodeAsync(
        string address, CancellationToken ct = default)
    {
        address = address.Trim();
        if (string.IsNullOrWhiteSpace(address))
            return null;

        // 1. In-memory/disk cache first. This is what makes repeated
        // optimization clicks cheap and prevents repeated Nominatim calls.
        if (TryGetCached(address, out var cached))
            return cached;

        var url =
            "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&countrycodes=fi&q=" +
            Uri.EscapeDataString(address);

        await WaitForNominatimSlotAsync(ct);

        using var response = await Http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Do NOT automatically retry a Nominatim 429. Retrying immediately
            // would be exactly the behavior that can prolong a temporary block.
            throw new HttpRequestException(
                "Osoitehakupalvelu palautti 429 Too Many Requests -vastauksen. " +
                "Ohjelma ei lähetä uusia yrityksiä automaattisesti. Odota hetki " +
                "ja yritä kerran uudelleen.",
                null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.GetArrayLength() == 0)
            return null;

        var x = doc.RootElement[0];
        var point = new GeoPoint(
            double.Parse(x.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture),
            double.Parse(x.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture),
            x.GetProperty("display_name").GetString() ?? address);

        // Cache only successful results.
        PutCached(address, point);
        return point;
    }

    public static async Task<RouteResult> OptimizeOpenRouteAsync(
        GeoPoint start,
        IReadOnlyList<GeoPoint> stops,
        GeoPoint? end = null,
        CancellationToken ct = default)
    {
        if (stops.Count == 0)
            return new RouteResult(Array.Empty<int>(), 0, 0);

        var points = new List<GeoPoint> { start };
        points.AddRange(stops);
        if (end is not null)
            points.Add(end);

        var coords = string.Join(";",
            points.Select(p =>
                $"{p.Longitude.ToString(CultureInfo.InvariantCulture)}," +
                $"{p.Latitude.ToString(CultureInfo.InvariantCulture)}"));

        var routeOptions = end is null
            ? "source=first&destination=any&roundtrip=false&overview=false"
            : "source=first&destination=last&roundtrip=false&overview=false";

        // Exactly ONE routing request for the whole workday. We don't retry
        // a 429 against another public endpoint, because doing so could look
        // like bypassing the first server's rate limit.
        var url =
            $"https://routing.openstreetmap.de/routed-car/trip/v1/driving/{coords}?{routeOptions}";

        try
        {
            using var response = await Http.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException(
                    "Reitityspalvelu palautti 429 Too Many Requests -vastauksen. " +
                    "Ohjelma ei yritä samaa reittiä automaattisesti uudelleen.",
                    null, response.StatusCode);
            }

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var code) || code.GetString() != "Ok")
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new InvalidOperationException(
                    "Reitityspalvelu ei pystynyt laskemaan reittiä." +
                    (string.IsNullOrWhiteSpace(message) ? "" : $" {message}"));
            }

            var wps = root.GetProperty("waypoints").EnumerateArray().ToList();
            var lastInputIndex = points.Count - 1;

            var ordered = wps
                .Select((wp, inputIndex) => new
                {
                    InputIndex = inputIndex,
                    OptimizedIndex = wp.GetProperty("waypoint_index").GetInt32()
                })
                .Where(x => x.InputIndex > 0 &&
                            (end is null || x.InputIndex < lastInputIndex))
                .OrderBy(x => x.OptimizedIndex)
                .Select(x => x.InputIndex - 1)
                .ToArray();

            if (ordered.Length != stops.Count)
                throw new InvalidOperationException(
                    "Reitityspalvelun optimointitulos oli puutteellinen.");

            var trip = root.GetProperty("trips")[0];

            return new RouteResult(
                ordered,
                trip.GetProperty("distance").GetDouble(),
                trip.GetProperty("duration").GetDouble());
        }
        catch (HttpRequestException)
        {
            throw;
        }
    }
}
