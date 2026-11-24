#Agent-First ERP
Vizija: docs/vision.md. Arhitektura: docs/architecture.md. Specifikacije: docs/specs/.
Stack: C# / ASP.NET Core, PostgreSQL, React + TypeScript, MCP, CLI.
Slojevi: Domain -> Application -> Infrastructure -> Api. Web, CLI i MCP su tanki klijenti nad istim Application slojem.
Komande: `dotnet build`, `dotnet test`.
Pravilo: AI nije feature ERP-a, AI je korisnik ERP-a.
Svaki upit mora biti tenant-scoped.
Svaka validacija i svako poslovno pravilo mora biti konfigurabilno po tenantu: mi isporučujemo zadanu vrijednost, tenant je može promijeniti. Novo pravilo se ne smije tvrdo ukodirati; vidi ADR o konfigurabilnim pravilima u docs/adr/.
Korisnik je agent tvrtke: mora jednostavno koristiti sustav, postaviti poslovna pravila i generirati izvjestaje (A01, RP01). Svaki spec koji dodaje poslovne podatke dodaje i izvjestaje za njih u katalog (`report_list`) i spominje nove alate u `guide` playbookovima.
