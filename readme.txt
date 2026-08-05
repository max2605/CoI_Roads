Highway Roads 0.3.1
===================

Kompatibel mit Captain of Industry v0.8.6 bis v0.8.6c.

Highway Roads erweitert Captain of Industry um flüssig planbare, zweispurige
Autobahnen. Fahrzeuge wählen geeignete Autobahnrouten automatisch, bleiben auch
in Kurven sauber auf ihrer Spur und verlassen die Fahrbahn wieder in Richtung
ihres tatsächlichen Ziels. Auf passenden Autobahnabschnitten erreichen sie bis
zu 140 % ihrer normalen Maximalgeschwindigkeit; gleichzeitig sinkt ihr
Wartungsbedarf auf 50 % des Basiswerts.

Die Strecke wird mit der öffentlichen Pfadplaner-API des Trains-DLC berechnet,
erzeugt aber echte native Straßenobjekte und keine Gleise. Mehrere Pivots,
S-Kurven und nahtlose Fortsetzungen erlauben lange, organisch verlaufende
Straßen ohne starre 45-Grad-Beschränkung. Ziele auf verschiedenen Geländehöhen
werden durch sanfte Straßenrampen verbunden.

Autobahn bauen
--------------

1. Im Fahrzeug-Menü die Kategorie „Autobahnen“ öffnen.
2. „Autobahn bauen“ auswählen.
3. Mit Linksklick den Anfang setzen.
4. Weitere einzelne Linksklicks setzen Pivots; der erweiterte Schienenplaner
   erzeugt dazwischen auch wiederholte Links-/Rechtskurven und S-Kurven.
5. Mit einem Doppelklick am letzten Pivot die komplette Vorschau bauen.
6. Rechtsklick verwirft die noch nicht gebaute Strecke. Ein Rechtsklick ohne
   aktive Planung schließt das Werkzeug.
7. Wird beim bestätigenden Doppelklick Umschalt/Shift gehalten, beginnt nach
   erfolgreichem Bau automatisch eine neue Strecke exakt am Endpunkt und in
   derselben Tangentenrichtung. Zwischen beiden Strecken entsteht kein Knick.

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

Der Mod formt das Gelände nicht selbst. Die vorhandene Geländehöhe und der
freie Bauraum müssen eine gültige Rampe zulassen. Zusätzlich zur nativen
Planung wird der volle vier Kacheln breite Straßenkorridor auf beidseitigen
Bodenkontakt innerhalb der nativen Toleranz, Kartenbegrenzungen und belegende
Gebäude oder andere Entities geprüft. Kein Straßenpunkt darf über dem Gelände
schweben oder in das Gelände schneiden; Wasser zählt nicht als Bodenstütze.
Geländerampen sind keine Brücken: Eine Überdeckung mit einer bestehenden
Autobahn bleibt auch bei unterschiedlicher Höhe gesperrt; Verbindungen sind nur
an einem ausgewählten freien Straßenende zulässig. Mit älteren Versionen bereits
gebaute schwebende Stücke werden nicht rückwirkend verändert und müssen vor der
Neuplanung abgerissen werden.
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
verglichen. Mit „Drehen“ lässt sich in 22,5-Grad-Schritten insbesondere die
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
Ziele, etwa bei Baggeraufträgen. Bis zu 8 sinnvoll sortierte
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
