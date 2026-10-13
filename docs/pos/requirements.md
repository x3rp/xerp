# POS modul — zahtjevi vlasnika (2026-10-13)

Izvorni popis vlasnika, nepromijenjen. POS je nadogradnja na ERP i ima zaseban frontend. Plan po specovima: `PLAN.md` (P01–P10).

## 1. Artikli i konfiguracija POS-a
- Odabir artikala koji se prikazuju na POS-u.
- Brzi prikaz artikala po kategorijama i prilagodljiv raspored tipki.
- Pretraživanje artikala po nazivu i barkodu.
- Podrška za artikle s opcijama i dodacima (npr. dodatno mlijeko).
- Definiranje normativa i sastojaka za artikle koji se pripremaju.
- Definiranje cijena, poreza i načina naplate kroz postojeće matične podatke.

## 2. Računi i narudžbe
- Otvaranje novog POS računa.
- Dodavanje i uklanjanje stavki s računa.
- Promjena količine i cijene uz odgovarajuće ovlasti.
- Dodavanje napomena uz pojedinu stavku.
- Spremanje računa kao otvorene narudžbe bez naplate.
- Nastavak rada na prethodno otvorenom računu.
- Storniranje stavke ili cijele narudžbe uz evidentiranje razloga.
- Primjena popusta na stavku ili cijeli račun.
- Podjela računa na više računa.
- Spajanje računa.
- Premještanje računa između stolova.

## 3. Stolovi i posluživanje
- Grafički prikaz stolova po prostoru ili zoni.
- Status stola: slobodan, zauzet, narudžba u pripremi, račun zatražen.
- Otvaranje računa za stol.
- Premještanje gosta ili računa na drugi stol.
- Evidencija konobara zaduženog za račun.
- Prijenos otvorenog računa drugom konobaru.

## 4. Priprema narudžbi
- Slanje narudžbe na šank ili u kuhinju.
- Usmjeravanje artikala prema mjestu pripreme.
- Prikaz narudžbi na ekranu za pripremu (KDS) ili ispis na pisaču.
- Označavanje stavki kao zaprimljenih, u pripremi i gotovih.
- Slanje naknadno dodanih stavki bez ponovnog ispisa cijele narudžbe.
- Prikaz napomena uz stavke.
- Evidencija vremena narudžbe i trajanja pripreme.

## 5. Naplata i računi
- Naplata gotovinom.
- Naplata karticom.
- Kombinirana naplata, npr. dio gotovinom, dio karticom.
- Evidencija dovršenih i neuspjelih plaćanja.
- Izračun iznosa za povrat gotovine.
- Izdavanje računa i ponovni ispis kopije.
- Storno ili povrat nakon naplate uz odgovarajuće ovlasti i evidenciju.
- Evidencija popusta, poreza i načina plaćanja.
- Integracija s fiskalizacijom prema primjenjivim hrvatskim propisima.
- Integracija s kartičnim terminalom ako se želi automatizirana naplata.

## 6. Blagajna i smjene
- Otvaranje smjene i početni iznos gotovine.
- Evidencija uplata i isplata iz blagajne.
- Predaja smjene drugom djelatniku.
- Zaključivanje smjene.
- Izračun očekivanog stanja gotovine.
- Unos stvarno prebrojene gotovine.
- Pregled razlike između očekivanog i stvarnog stanja.
- Izvještaj o prometu po konobaru, smjeni i načinu plaćanja.

## 7. Integracija sa zajedničkim ERP backendom
- Potvrđena prodaja evidentira se u zajedničkom prodajnom modulu.
- Zalihe se ažuriraju kroz postojeću skladišnu logiku.
- Potrošnja sastojaka obračunava se prema normativima.
- Storniranja i povrati ispravno korigiraju poslovne evidencije.
- POS koristi postojeće artikle, cijene, poreze i jedinice mjere.
- ERP izvještaji uključuju POS promet bez zasebnog uvoza podataka.
- Sve operacije poštuju postojeću autorizaciju, izolaciju tenantova i audit.
- Sprječavanje dvostruke naplate ili dvostrukog evidentiranja prodaje.

## 8. Pouzdanost i integracije s uređajima
- Podrška za dodirni zaslon i različite veličine ekrana.
- Podrška za barkod čitač gdje je primjenjivo.
- Podrška za pisač računa.
- Podrška za pisače narudžbi ili ekran kuhinje/šanka.
- Obrada prekida veze i neuspjelih zahtjeva bez dupliciranja operacija.
- Jasna poruka ako naplata, ispis ili fiskalizacija nisu uspjeli.
- Evidencija i siguran nastavak nedovršenih operacija.
- Opcionalni offline rad s naknadnom sinkronizacijom.
