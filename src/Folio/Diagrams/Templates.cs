namespace Folio.Diagrams;

/// <summary>Vorlagen für Prozess- und Automatisierungsarbeit. Platzhalter sind bewusst neutral (keine Firmen, keine Namen).</summary>
public static class Templates
{
    public const string Flow = @"```mermaid
flowchart LR
  A([Start]) --> B[Schritt 1]
  B --> C{Entscheidung?}
  C -->|ja| D[Schritt 2]
  C -->|nein| E[Alternative]
  D --> F([Ende])
  E --> F
  %% zeit B 2 min
  %% zeit D 5 min
```";

    /// <summary>Bahnen am Beispiel einer Online-Bestellung (Kunde → Onlineshop → Lager → Versand).</summary>
    public const string Swimlane = @"```mermaid
---
title: Online-Bestellung abwickeln
---
flowchart LR
  subgraph Kunde
    A([Bestellung aufgeben]) --> B[Zahlungsart wählen]
  end
  subgraph Web [Onlineshop]
    C[Bestellung prüfen]
  end
  subgraph Lager [Lagersystem]
    D[(Bestand abfragen)]
  end
  subgraph Versand
    E[Ware zusammenstellen] --> F{Alles vorrätig?}
    F -->|ja| G[Paket packen]
    F -->|nein| H[Ware nachbestellen]
    G --> Z([Paket verschickt])
  end
  B --> C --> D --> E
  H -.-> D
  %% zeit B 1 min
  %% zeit C 10 s
  %% zeit D 5 s
  %% zeit E 10 min
  %% zeit G 5 min
  %% liegezeit H D 2 h
```";

    public const string Sequence = @"```mermaid
sequenceDiagram
  autonumber
  actor K as Kunde
  participant S as Onlineshop
  participant Z as Zahlungsdienst
  participant L as Lager
  K->>S: Bestellung absenden
  S->>S: Warenkorb prüfen
  S->>Z: Zahlung anfragen
  Z-->>S: Zahlung bestätigt
  S->>L: Artikel reservieren
  L-->>S: Artikel reserviert
  alt alles vorrätig
    S->>K: Bestellbestätigung
  else Artikel fehlt
    S->>K: Lieferzeit mitteilen
  end
  Note over S,Z: Zahlungsdaten bleiben beim Zahlungsdienst
  %% zeit 3 3 s
  %% zeit 5 2 s
```";

    public const string Bars = @"```mermaid
xychart-beta
  title ""Durchlaufzeit je Schritt (Minuten)""
  x-axis [Prüfen, Zusammenstellen, Packen, Übergabe]
  y-axis ""Minuten""
  bar [1, 10, 5, 3]
```";

    public const string Pie = @"```mermaid
pie title Zeitanteile im Prozess
  ""Bearbeitung"" : 25
  ""Warten auf Ware"" : 60
  ""Prüfung"" : 15
```";

    public const string ProcessProfile = @"## Prozess-Steckbrief: Name des Prozesses

| | |
| --- | --- |
| **Ziel** | Was soll am Ende herauskommen? |
| **Auslöser** | z. B. neue Bestellung |
| **Ergebnis** | z. B. verschicktes Paket |
| **Verantwortlich** | Rolle |
| **Systeme** | z. B. Onlineshop, Lagersystem, Versanddienst |
| **Häufigkeit** | z. B. 30 × pro Woche |
| **Dauer heute** | manuell: … / automatisiert: … |

### Ablauf

1. Schritt
2. Schritt
3. Schritt

### Risiken und Ausnahmen

- [ ] Was passiert, wenn Daten fehlen?
- [ ] Datenschutz geprüft
";

    public const string Sipoc = @"## SIPOC: Name des Prozesses

| Lieferanten | Eingaben | Prozess | Ausgaben | Kunden |
| --- | --- | --- | --- | --- |
| Kunde | Bestellung | 1. Bestellung prüfen | Bestätigung | Kunde |
| Lager | Ware | 2. Ware zusammenstellen | Paket | Versanddienst |
| | | 3. Paket verschicken | Sendungsnummer | |
";

    public const string Raci = @"## RACI: Name des Prozesses

R = durchführend · A = verantwortlich · C = wird gefragt · I = wird informiert

| Aufgabe | Shop | Lager | Versand | Leitung |
| --- | --- | --- | --- | --- |
| Bestellung prüfen | R | I | | A |
| Ware zusammenstellen | I | R | C | A |
| Paket verschicken | I | | R | A |
";

    public const string AgentProfile = @"## Agent-Steckbrief: Name des Agents

| | |
| --- | --- |
| **Aufgabe** | Was erledigt der Agent? |
| **Auslöser** | z. B. neue Bestellung im Shop |
| **Werkzeuge / Konnektoren** | z. B. E-Mail, Shop-System, Lager-Schnittstelle |
| **Eingaben** | z. B. Bestellnummer, Artikelliste |
| **Ausgaben** | z. B. Bestätigung, Statusmeldung |
| **Grenzen** | Was darf der Agent nicht? |
| **Eskalation** | Wann übernimmt ein Mensch? |
| **Kennzahlen** | Durchlaufzeit, Fehlerquote, Fälle pro Tag |

### Ablauf

```mermaid
flowchart LR
  T([Auslöser]) --> A[Agent liest Daten] --> W[Werkzeug aufrufen] --> E([Ergebnis])
```

### Offene Punkte

- [ ] Berechtigungen geklärt
- [ ] Testfälle beschrieben
";
}
