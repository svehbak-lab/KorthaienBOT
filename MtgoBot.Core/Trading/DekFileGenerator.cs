using System.Text;
using System.Xml;
using MtgoBot.Core.Data;

namespace MtgoBot.Core.Trading;

/// <summary>
/// Generates MTGO .dek files from a bot's buylist.
/// MTGO matches cards by CatID (our card_id) — which is set- and foil-specific —
/// so importing a .dek into a trade auto-adds the exact printings the bot wants
/// from the customer's binder. No need to read set codes or foil status from the UI.
/// </summary>
public static class DekFileGenerator
{
    /// <summary>
    /// Writes a .dek file containing the given buylist entries.
    /// Returns the full path to the written file.
    /// </summary>
    public static string WriteBuylistDek(IEnumerable<BuylistEntry> buylist, string outputPath)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false), // no BOM — MTGO is picky
            OmitXmlDeclaration = false
        };

        using (var writer = XmlWriter.Create(outputPath, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Deck");
            writer.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            writer.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");

            writer.WriteElementString("NetDeckID", "0");
            writer.WriteElementString("PreconstructedDeckID", "0");

            foreach (var entry in buylist)
            {
                if (entry.QtyNeeded <= 0) continue;

                writer.WriteStartElement("Cards");
                writer.WriteAttributeString("CatID", entry.CardId);
                writer.WriteAttributeString("Quantity", entry.QtyNeeded.ToString());
                writer.WriteAttributeString("Sideboard", "false");
                writer.WriteAttributeString("Name", entry.CardName);
                writer.WriteAttributeString("Annotation", "0");
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // Deck
            writer.WriteEndDocument();
        }

        return outputPath;
    }
}
