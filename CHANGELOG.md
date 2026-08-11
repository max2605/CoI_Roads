# Changelog

## 0.5.0 – 2026-08-11

- Eine acht Kacheln breite T3-Straßenfamilie mit einer nativen vier Kacheln breiten Fahrspur pro Richtung für schwere Fahrzeuge ergänzt.
- Eine 16 Kacheln breite T4-Straßenfamilie mit zwei nativen vier Kacheln breiten Fahrspuren pro Richtung für Geraden, Kurven, S-Kurven sowie G4-/G8-Steigungen ergänzt.
- Separate, lokalisierte Bauwerkzeuge für T1/T2, T3 und T4 einschließlich profilbreiter Vorschau, Prüfung, Stützen, Einrasten und breitenabhängiger Baukosten ergänzt.
- Vollständige MultiLangLib-Kataloge für alle 21 offiziellen Spielsprachen plus neutrales Portugiesisch ergänzt; jeder der 22 Kataloge enthält dieselben 56 sichtbaren Texte.
- Deterministische T4-Spurwahl ergänzt: schnellere Fahrzeuge bevorzugen die inneren Überholspuren, langsamere Fahrzeuge die äußeren Fahrspuren.
- Breite Straßenenden werden als gemeinsames physisches Anschlussziel behandelt, während die Routenfindung alle Fahrspur-Endpunkte sieht.
- Alle vorhandenen T1/T2-Prototyp-IDs, Geometrien und Spurfolgen bleiben erhalten; T3 und T4 verwenden neue, speicherstabile V1-IDs.
- Dynamische Spurwechsel während der Fahrt bleiben deaktiviert, weil Captain of Industry 0.8.6c keine sichere Reservierung mit Prüfung des rückwärtigen Abstands bereitstellt. Version 0.5.0 wählt die Spur vor Fahrtbeginn.
- Kompatibilität mit Captain of Industry 0.8.6c geprüft. Die Mod kann zu Spielständen hinzugefügt, nach dem Bau von Straßenobjekten aber nicht mehr entfernt werden.

## 0.4.1 – 2026-08-07

- Konfigurierbare Heben-/Senken-Steuerung des Zugbauwerkzeugs samt E-/Q-Standardbelegung und Werkzeugschaltflächen ergänzt.
- Betonstützen an den nativen Pfeilerpositionen für geneigte und erhöhte flache Straßenstücke ergänzt.
- Die Cursorhöhe lässt sich in Ein-Kachel-Schritten bis zur nativen Sechs-Kachel-Grenze der Zugpfeiler einstellen.
- Zusammenhängende Pläne mit sichtbar gestützten G0-Stücken werden atomar fertiggestellt; bodennahe Pläne behalten ihre normalen Materialkosten.
- Gelände kann die dünne Fahrbahndecke am oberen Rampenende nicht mehr verdecken.

## 0.4.0 – 2026-08-06

- Asphalt als lager- und transportierbares Schüttgut sowie ein Rezept aus 19 Kies und 1 Schweröl für Industriemischer I und II ergänzt.
- Kostenlose Platzierung durch native Baustellen und Lkw-Anlieferung für horizontale Straßenstücke und Knoten ersetzt.
- Längenabhängige Straßenkosten und flächenabhängige Kosten für Kreuzungen und Kreisverkehre ergänzt.
- Pro aufgerundeter horizontaler Kachel werden 2 Kies für das Fundament und 1 Asphalt für die Oberfläche benötigt.
- Echte G4-/G8-Rampen dürfen tieferes trockenes Gelände überspannen; flache G0-Flächen bleiben geländegebunden.
- Geneigte Teilstücke werden sofort fertiggestellt, damit sie ihre eigene Materialanlieferung nicht blockieren.
- Rampenplanung, Endpunkt-Einrasten, Begrenzung zusätzlicher Ausfahrtsuchen und Routing über fertiggestellte Netze verbessert.

## 0.3.4 – 2026-08-06

- Direktes Einrasten an sichtbaren Kreuzungen und Kreisverkehren stabilisiert; nahe einzelne Arme haben Vorrang.
- Kollisionsprüfung von Kreisverkehren um den längeren Einfädel- und Ausfahrtsbereich erweitert.
- Fahrzeuge behalten bei geländebedingten Routenaktualisierungen ihre aktuelle native Straßenroute.
- Routing-Protokolle pro Fahrzeug und Simulationsschritt entfernt und zusätzliche Routensuchen auf die besten Kandidaten begrenzt.

## 0.3.1 – 2026-08-06

- Geländefolgende Autobahnrampen mit nativen Steigungen von 12,5 und 25 Prozent ergänzt.
- Horizontale Übergänge an Rampenanfängen, Rampenenden, Wegpunkten und Netzanschlüssen ergänzt.
- Prüfung der gesamten Straßenbreite auf Geländeauflage, Wasser, Kartenrand und belegtes Gelände ergänzt.
- Dreidimensionale Spurführung durch Kurven und Höhenänderungen ergänzt.
- Automatische Versuche mit flacher Zufahrt an freien Straßenenden ergänzt.

## 0.2.0 – 2026-08-05

- Dreiarmige T-Kreuzungen, vierarmige Kreuzungen und vierarmige Kreisverkehre für Rechtsverkehr ergänzt.
- Alle 16 Platzierungsrichtungen in 22,5-Grad-Schritten ergänzt.
- Exaktes Einrasten an freien Netzanschlüssen und automatische Auswahl eines passenden freien Arms ergänzt.
- Geometrie-Smoke-Tests für Straßen, Kreuzungen, Kreisverkehre, Einrasten, Routing und Kollisionsprüfung ergänzt.
- Das frühere separate Rampenwerkzeug durch das einheitliche Straßenbau-Netzwerk ersetzt.

## 0.1.0 – 2026-08-05

- Fließend planbare zweispurige Autobahnen mit Kurven, wiederholten S-Kurven und tangentenstetigen Erweiterungen ergänzt.
- Bau über mehrere Wegpunkte und exakte Endpunkt-Fortsetzung per Umschalt-Doppelklick ergänzt.
- Automatischen Vergleich zwischen normalen Routen und erreichbaren Autobahnalternativen einschließlich Ein- und Ausfahrt ergänzt.
- Spurgebundene Kurvenfahrt, bis zu 140 Prozent Straßengeschwindigkeit und 50 Prozent Basiswartungsbedarf ergänzt.
- Rückfall auf die normale Spielroute ergänzt, wenn keine geeignete Autobahnroute vorhanden ist.
