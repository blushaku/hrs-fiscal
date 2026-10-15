using System.Collections.Generic;

namespace Hrs.Fiscal.Tray;

/// <summary>Texts in Albanian (default), Serbian (Latin) and English; the language is the fiscal server's default.</summary>
internal static class Texts
{
    private static readonly Dictionary<string, string[]> T = new()
    {
        // key: sq, sr, en
        ["online"] = ["ATK: në linjë", "ATK: na mreži", "ATK: online"],
        ["pending"] = ["ATK: {0} kuponë po dërgohen", "ATK: šalje se {0} računa", "ATK: {0} receipts being sent"],
        ["offline"] = ["ATK: JASHTË LINJE", "ATK: VAN MREŽE", "ATK: OFFLINE"],
        ["checking"] = ["ATK: po kontrollohet", "ATK: provera u toku", "ATK: checking"],
        ["notready"] = ["Fiscal Client: nuk është gati", "Fiscal Client: nije spreman", "Fiscal Client: not ready"],
        ["server"] = ["Pa lidhje me serverin fiskal", "Nema veze sa fiskalnim serverom", "No connection to the fiscal server"],
        ["service"] = ["Fiscal Client nuk punon", "Fiscal Client ne radi", "Fiscal Client is not running"],

        ["atk.title"] = ["Pa lidhje me ATK-në", "Nema veze sa ATK", "No connection to ATK"],
        ["atk.since"] = ["Që nga {0}.", "Od {0}.", "Since {0}."],
        ["atk.body"] = ["Kuponët lëshohen normalisht, shënohen OFFLINE dhe dërgohen automatikisht në ATK kur kthehet lidhja.",
                        "Računi se izdaju normalno, označavaju se OFFLINE i automatski se šalju ATK kada se veza vrati.",
                        "Receipts are issued as usual, marked OFFLINE and sent to ATK automatically when the connection returns."],
        ["atk.pending"] = ["Kuponë në pritje: {0}", "Računi na čekanju: {0}", "Receipts waiting: {0}"],
        ["atk.deadline"] = ["Afati 48 orë për dërgim: {0}", "Rok od 48 sati za slanje: {0}", "48-hour deadline for sending: {0}"],
        ["atk.footer"] = ["Nëse zgjat, njoftoni administratorin.", "Ako potraje, obavestite administratora.", "If it lasts, inform the administrator."],

        ["server.title"] = ["Pa lidhje me serverin fiskal", "Nema veze sa fiskalnim serverom", "No connection to the fiscal server"],
        ["server.body"] = ["Ky vendpunës nuk arrin serverin fiskal. Faturat e këtij vendpunësi nuk mund të fiskalizohen derisa të kthehet lidhja.",
                           "Ovo radno mesto ne dostiže fiskalni server. Računi ovog radnog mesta ne mogu da se fiskalizuju dok se veza ne vrati.",
                           "This workstation cannot reach the fiscal server. Folios of this workstation cannot be fiscalized until the connection returns."],
        ["server.since"] = ["Kontakti i fundit: {0}.", "Poslednji kontakt: {0}.", "Last contact: {0}."],

        ["service.title"] = ["Fiscal Client nuk punon", "Fiscal Client ne radi", "The Fiscal Client is not running"],
        ["service.body"] = ["Shërbimi «Opera Cloud Fiscal Solution - Kosovo - Client» nuk përgjigjet në këtë kompjuter. Faturat e këtij vendpunësi nuk mund të fiskalizohen.",
                            "Servis „Opera Cloud Fiscal Solution - Kosovo - Client” ne odgovara na ovom računaru. Računi ovog radnog mesta ne mogu da se fiskalizuju.",
                            "The service \"Opera Cloud Fiscal Solution - Kosovo - Client\" does not answer on this computer. Folios of this workstation cannot be fiscalized."],
        ["notify"] = ["Njoftoni administratorin.", "Obavestite administratora.", "Inform the administrator."],

        ["back.title"] = ["Lidhja me ATK-në u rikthye", "Veza sa ATK je vraćena", "Connection to ATK restored"],
        ["back.body"] = ["Kuponët në pritje dërgohen automatikisht.", "Računi na čekanju se šalju automatski.", "Waiting receipts are sent automatically."],
        ["back.server"] = ["Lidhja me serverin fiskal u rikthye.", "Veza sa fiskalnim serverom je vraćena.", "Connection to the fiscal server restored."],

        ["hide"] = ["Fshihe", "Sakrij", "Hide"],
        ["menu.show"] = ["Shfaq statusin", "Prikaži status", "Show status"],
        ["menu.page"] = ["Hap faqen e statusit", "Otvori stranicu statusa", "Open the status page"],
        ["status.ok"] = ["Gjithçka në rregull: ATK në linjë, asnjë kupon në pritje.", "Sve je u redu: ATK na mreži, nema računa na čekanju.", "All fine: ATK online, no receipts waiting."],
        ["status.pending"] = ["ATK në linjë. {0} kuponë po dërgohen tani.", "ATK na mreži. {0} računa se upravo šalje.", "ATK online. {0} receipts are being sent now."],
        ["status.notready"] = ["Fiscal Client nuk është lidhur ose regjistruar ende në ATK.", "Fiscal Client još nije uparen ili registrovan u ATK.", "The Fiscal Client is not paired or registered with ATK yet."],
        ["status.checking"] = ["Statusi i ATK-së po kontrollohet.", "Status ATK se proverava.", "ATK's status is being checked."],
    };

    public static string Get(string language, string key, params object[] args)
    {
        var i = language switch { "sr" => 1, "en" => 2, _ => 0 };
        var text = T.TryGetValue(key, out var v) ? v[i] : key;
        return args.Length == 0 ? text : string.Format(text, args);
    }
}
