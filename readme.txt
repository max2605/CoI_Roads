Highway Roads 0.1.0
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
Straßen ohne starre 45-Grad-Beschränkung.

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

Automatische Zufahrt
--------------------

Separate Auf- und Abfahrten sind nicht mehr erforderlich. Fahrzeuge können an
jedem gerichteten Segmentübergang auf eine erreichbare Autobahn wechseln und
sie an einem späteren Übergang wieder verlassen. Alte Rampen bleiben in
bestehenden Spielständen ladefähig, erscheinen aber nicht mehr im Baumenü.

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
Ziele, etwa bei Baggeraufträgen. Bis zu 8 sinnvoll sortierte Ausfahrten werden
geprüft, bevor auf den gesicherten Engine-Pfad zurückgefallen wird. Gebogene
Spuren bleiben in beiden Fahrtrichtungen im Graphen; ihre sichere erste
Zielentfernung und ihre tatsächliche Weglänge werden getrennt bewertet.

Exakte Lane-Endpunkte und befahrbare Geländezugänge werden getrennt verwaltet.
Ist ein Endpunkt wegen Straßenbreite oder Fahrzeug-Clearance kein gültiges
Geländefeld, verwendet der Direktor in beiden Fahrtrichtungen dieselbe kleine
lokale Ersatzpunktsuche der Engine. Dadurch funktionieren Hin- und Rückfahrt
symmetrisch, ohne den Autobahnabschnitt oder seine Spurbindung zu verändern.

Wirkung
-------

  - Alle straßenfähigen Fahrzeuge fahren auf Mod-Straßen mit 140 %
    Maximalgeschwindigkeit.
  - Autobahnfahrzeuge folgen den Lane-Trajektorien auch in Kurven. Dabei wird
    nur die seitliche Abweichung korrigiert, sodass sie an gewählten
    Segmentübergängen die Autobahn wieder zum Ziel verlassen können.
  - Der Wartungsverbrauch auf Mod-Straßen beträgt 50 %.
  - Niedriger Treibstoff und Defekte wirken weiterhin normal.

Kompatibilität
--------------

Alle alten Prototype-IDs einschließlich der Rampen bleiben zum Laden früherer
Spielstände registriert, erscheinen aber nicht mehr im Baumenü. Bereits
platzierte Rampen dürfen stehen bleiben, werden für neue Routen jedoch nicht
benötigt.

Der Mod kann einem bestehenden Spielstand hinzugefügt werden. Sobald ein
Straßenbauteil gespeichert wurde, darf der Mod aus diesem Spielstand nicht
mehr entfernt werden.

Lizenzhinweis
-------------

Das Paket enthält keinen Captain-of-Industry-Quellcode und keine veränderten
Spielassets. Es enthält eigenständig geschriebenen Mod-Code und verwendet die
öffentliche Mod-/Laufzeit-API des Spiels.
