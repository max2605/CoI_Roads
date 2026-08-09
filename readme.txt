Highway Roads 0.4.1
===================

Kompatibel mit Captain of Industry v0.8.6 bis v0.8.6c.

Highway Roads erweitert Captain of Industry um flüssig planbare, zweispurige
Autobahnen. Fahrzeuge wählen geeignete Autobahnrouten automatisch, bleiben auch
in Kurven sauber auf ihrer Spur und verlassen die Fahrbahn wieder in Richtung
ihres tatsächlichen Ziels. Auf passenden Autobahnabschnitten erreichen sie bis
zu 140 % ihrer normalen Maximalgeschwindigkeit; gleichzeitig sinkt ihr
Wartungsbedarf auf 50 % des Basiswerts.

Ressourcen und Straßenbau
-------------------------

Asphalt ist ein neues loses, lager- und transportfähiges Produkt. Industrielle
Mischer I und II stellen aus 19 Kies und 1 Schweröl insgesamt 20 Asphalt her.
Das entspricht einer vereinfachten Asphaltmischung aus 95 % Gesteinskörnung und
5 % bitumenartigem Bindemittel.

Neue Autobahnen sind keine kostenlosen Sofortbauten mehr. Für jede aufgerundete
waagerechte Längenkachel werden 2 Kies für Unterbau und Tragschicht sowie
1 Asphalt für Binder- und Deckschicht benötigt. Waagerechte Geraden und Kurven
verwenden ihre tatsächliche Länge. Geneigte G4/G8-Teilstücke sind bereits
kostenfrei. Enthält ein zusammenhängender, vollständig vorvalidierter Bauplan
mindestens ein sichtbar auf Pfeilern stehendes flaches G0-Teilstück, wird der
gesamte Plan atomar sofort fertiggestellt. Dadurch kann kein unerreichbarer
Bauplatz den zusammenhängenden Bau blockieren. Reine bodennahe Pläne behalten
ihre normalen Materialkosten. T-Kreuzung, +-Kreuzung und
Kreisverkehr besitzen ansteigende, flächenbasierte Kosten. Die normalen
Baulaster liefern die beiden Materialien an. Der native Bauplatz bündelt
Kiesunterbau, Tragschicht und
Asphaltdecke in einem speicherbaren Baufortschritt. Erst fertiggestellte Straßen
werden vom Spiel für Fahrzeuge freigegeben. Schnellbau und Abbruch verwenden
weiterhin die normalen Regeln des Spiels.

Die Strecke wird mit der öffentlichen Pfadplaner-API des Trains-DLC berechnet,
erzeugt aber echte native Straßenobjekte und keine Gleise. Mehrere Pivots,
S-Kurven und nahtlose Fortsetzungen erlauben lange, organisch verlaufende
Straßen ohne starre 45-Grad-Beschränkung. Ziele auf verschiedenen Geländehöhen
werden durch sanfte Straßenrampen verbunden.

Autobahn bauen
--------------

1. Im Fahrzeug-Menü die Kategorie „Autobahnen“ öffnen.
2. „Autobahn bauen“ auswählen.
3. Mit E wird die aktuelle Bauhöhe um eine Kachel angehoben, mit Q um eine
   Kachel abgesenkt. Wie beim Gleisbau zeigen zwei Schaltflächen die belegten
   Tasten an; der Bereich reicht von 0 bis zur nativen Pfeilerhöhe von 6
   Kacheln. E und Q funktionieren vor dem Startpunkt und während der Planung.
4. Mit Linksklick den Anfang setzen.
5. Weitere einzelne Linksklicks setzen Pivots; der erweiterte Schienenplaner
   erzeugt dazwischen auch wiederholte Links-/Rechtskurven und S-Kurven.
6. Mit einem Doppelklick am letzten Pivot die komplette Vorschau bauen.
7. Rechtsklick verwirft die noch nicht gebaute Strecke. Ein Rechtsklick ohne
   aktive Planung schließt das Werkzeug.
8. Wird beim bestätigenden Doppelklick Umschalt/Shift gehalten, beginnt nach
   erfolgreichem Bau automatisch eine neue Strecke exakt am Endpunkt und in
   derselben Tangentenrichtung. Zwischen beiden Strecken entsteht kein Knick.

Zum Verlängern einer vorhandenen Autobahn darf der erste Klick bis zu 24
Kacheln geradlinig vor ihrem freien Ende liegen. Das Werkzeug wählt anhand der
Ausfahrtrichtung eindeutig das richtige Ende – auch bei nur einer Kachel langen
Segmenten und wenn der Klick bereits auf höherem Aufschüttgelände liegt.

Die anfängliche Startrichtung folgt automatisch der Maus und wird auf
22,5-Grad-Schritte gesnappt. Vor dem ersten Pivot kann „Drehen“ die
Startrichtung manuell wählen und „Spiegeln“ sie umkehren. Nach einem Pivot
bleibt die Anschlussrichtung absichtlich fest, damit keine Winkelbrüche
entstehen.

Geländerampen
-------------

Liegt ein neuer Pivot höher oder tiefer als der vorherige, plant das Werkzeug
automatisch eine echte Höhenrampe in die Straße. Der Planer verwendet die
nativen Steigungsstufen 12,5 % und 25 % und bevorzugt bei genügend Platz die
flachere Variante. Jeder Pivot und jeder Anschluss an eine Kreuzung bleibt
waagerecht; Anstieg und Gefälle liegen glatt dazwischen. Auf- und Abfahrt
funktionieren in beiden Fahrtrichtungen.

Der Mod formt das Gelände nicht selbst. Waagerechte G0-Stücke und echte
G4/G8-Rampen dürfen wie Gleise oder Förderbänder über tieferem trockenem Gelände
liegen. Unter der Fahrbahn erscheinen Betonstützen an den Pfeilerpositionen der
nativen Schienengeometrie; unterirdische Stützenanteile werden vom Gelände
verdeckt. Mit E und Q lässt sich die gewünschte Höhe gezielt einstellen. Der
Planer bevorzugt außerdem die Höhe des oberen Endes, damit die Rampe vor einer
Aufschüttung ansteigt, anstatt in sie hineinzulaufen.

Gelände darf die Fahrbahnebene nur um 0,02 Kacheln überragen. Da die sichtbare
Asphaltdecke höher liegt, bleiben sie und beide Randlinien auch am oberen Ende
einer Aufschüttung sichtbar. Liegt das Plateau höher, wird die Vorschau
abgelehnt und kann mit E angehoben werden.

Kartenbegrenzungen, Wasser sowie belegende Gebäude oder andere Entities werden
weiterhin abgelehnt. Eine Überdeckung mit einer bestehenden Autobahn bleibt
auch bei unterschiedlicher Höhe gesperrt; Verbindungen sind nur an einem
ausgewählten freien Straßenende zulässig. Auch gestützte waagerechte Abschnitte
sind möglich; die maximale Deckhöhe über dem lokalen Gelände beträgt wie beim
Gleisbau 6 Kacheln.
Bei einem freien Endpunkt probiert das Werkzeug automatisch alternative ebene
Anfahrtsrichtungen. Bleibt die Vorschau bei zu kurzer Strecke oder blockiertem
Korridor unbaubar, gibt ein weiter entfernter Pivot dem Planer mehr Platz für
Rampenanfang, Steigung und Rampenende.

Kreuzungen und Kreisverkehr
---------------------------

Im Autobahn-Menü stehen drei zusätzliche Knotenwerkzeuge bereit:

  - T-Kreuzung mit drei Armen und sechs gerichteten Fahrbeziehungen
  - +-Kreuzung mit vier Armen sowie Geradeaus-, Links- und Rechtsabbiegern
  - vierarmiger Kreisverkehr mit einheitlicher Fahrtrichtung für Rechtsverkehr

Die Knotenwerkzeuge rasten ausschließlich an freien Enden normaler
Autobahnsegmente ein. Zwei Kreuzungen beziehungsweise Kreisverkehre werden
nicht direkt miteinander verschnappt: Dazwischen muss ein kurzes
Autobahnsegment liegen; 8 bis 16 Kacheln Abstand sind empfehlenswert. Dadurch
hat die native Fahrzeugsteuerung zwischen zwei Konfliktbereichen genug Platz.
Der Mod erzwingt dafür einen Mindestabstand von 24 Kacheln zwischen den
Knotenmittelpunkten. Bei Überlappung oder zu geringem Abstand wird die Vorschau
rot; ein Bauklick erklärt, dass dazwischen ein kurzes Autobahnstück nötig ist.
Neue Autobahnen rasten beim ersten und letzten Punkt weiterhin an freien
Knotenarmen ein. Position, Fahrtrichtung und Spurtyp werden dabei exakt
verglichen. Ein Klick auf die sichtbare Kreuzung oder den Kreisverkehr wählt
automatisch den zum bisherigen Straßenverlauf passenden freien Arm; ein
direkter Klick nahe einem Arm hat Vorrang. Beim Kreisverkehr berücksichtigt
die Kollisionsprüfung zusätzlich die längere Ein- und Ausfädelung am Arm.
Mit „Drehen“ lässt sich in
22,5-Grad-Schritten insbesondere die
fehlende Seite der T-Kreuzung wählen. Belegte Mittelflächen verhindern das
Platzieren über Gebäuden; die Fahrspuren des Kreisverkehrs werden physisch
gemeinsam genutzt.

Kreuzungen sind derzeit ungeregelt: Der native Straßengraph des Spiels kennt
für kreuzende Mod-Fahrspuren keine Ampel- oder Vorfahrtsreservierung.

Automatische Zufahrt
--------------------

Separate Auf- und Abfahrten sind nicht erforderlich. Fahrzeuge können an
waagerechten Segmentübergängen auf eine erreichbare Autobahn wechseln und sie
an einem späteren Übergang wieder verlassen, wenn Straßen- und Geländehöhe
übereinstimmen. Geneigte oder frei über dem Gelände liegende Rampenknoten sind
keine Zugänge; dadurch entstehen keine vertikalen Fahrzeugsprünge.
Bei Terrainänderungen behalten Fahrzeuge auf dem Autobahnnetz ihre native
aktuelle Straßenroute. Zusatzsuchen sind auf die vier besten Kandidaten
begrenzt und erzeugen keine Logzeile pro Fahrzeug und Simulationsschritt.

Routing
-------

Für jeden Fahrzeugauftrag sichert der Mod zunächst den normalen Engine-Pfad,
einschließlich regulärer Straßen und Brücken. Start und Ziel werden danach an
den Verkehrsdirektor übermittelt. Er baut aus den platzierten Fahrspuren einen
gerichteten Autobahngraphen und prüft mehrere erreichbare Kombinationen:

  Geländeweg zum Autobahneinstieg
+ Autobahnweg bei 140 % Geschwindigkeit
+ Geländeweg vom Autobahnausstieg zum Ziel

Liegt diese Summe höchstens 10 % über dem Direktweg, wird die Autobahn
bevorzugt. Scheitert eine automatische Zufahrt, bleibt der zuvor gesicherte
Engine-Pfad erhalten. Der gewählte Autobahnabschnitt wird als ein
durchgehender nativer Road-Pfad gefahren; nur am echten Einstieg und am
gewählten Ausstieg wechselt das Fahrzeug zwischen Gelände und Straße. Die
Teilpfade bleiben vom Spiel speicherbare VehicleTerrainPathSegment- und
VehicleRoadPathSegment-Objekte.

Der Zielanflug übernimmt auch den nativen „nah genug“-Vertrag dynamischer
Ziele, etwa bei Baggeraufträgen. Bis zu 4 sinnvoll sortierte
Autobahnausstiege werden geprüft, bevor auf den gesicherten Engine-Pfad
zurückgefallen wird. Gebogene Spuren bleiben in beiden Fahrtrichtungen im
Graphen; ihre sichere erste Zielentfernung und ihre tatsächliche Weglänge werden
getrennt bewertet.

Exakte Lane-Endpunkte und befahrbare Geländezugänge werden getrennt verwaltet.
Ist ein Endpunkt wegen Straßenbreite oder Fahrzeug-Clearance kein gültiges
Geländefeld, verwendet der Direktor in beiden Fahrtrichtungen dieselbe kleine
lokale Ersatzpunktsuche der Engine. Dadurch funktionieren Hin- und Rückfahrt
symmetrisch, ohne den Autobahnabschnitt oder seine Spurbindung zu verändern.

Wirkung
-------

  - Alle straßenfähigen Fahrzeuge fahren auf Mod-Straßen mit 140 %
    Maximalgeschwindigkeit.
  - Auf normalen Autobahnsegmenten folgen Fahrzeuge den Lane-Trajektorien auch
    in Kurven sowie bergauf und bergab. Kreuzungen und Kreisverkehre behalten
    die native Lenkung, damit Übergänge zwischen ihren kurzen Abbiegespuren
    nicht zurückgesetzt werden.
  - Der Wartungsverbrauch auf Mod-Straßen beträgt 50 %.
  - Niedriger Treibstoff und Defekte wirken weiterhin normal.

Kompatibilität
--------------

Historische Rampen-IDs bleiben ausschließlich zum Laden früherer Spielstände
registriert. Sie erscheinen nicht im Baumenü und sind kein Teil neuer Routen.
Kreuzungen und Kreisverkehr verwenden stabile V1-IDs.

Der Mod kann einem bestehenden Spielstand hinzugefügt werden. Sobald ein
Straßenbauteil gespeichert wurde, darf der Mod aus diesem Spielstand nicht
mehr entfernt werden.

Lizenzhinweis
-------------

Das Paket enthält keinen Captain-of-Industry-Quellcode und keine veränderten
Spielassets. Es enthält eigenständig geschriebenen Mod-Code und verwendet die
öffentliche Mod-/Laufzeit-API des Spiels.
