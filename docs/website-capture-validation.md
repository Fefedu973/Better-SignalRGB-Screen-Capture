# Capture web indépendante du canvas

`WebsiteCaptureHost` possède son contrôleur WebView2 et un HWND sans activation ni entrée dans la barre des tâches, positionné hors du bureau virtuel. Le contrôleur reste visible pour Chromium ; son rendu ne dépend plus du chargement, de la visibilité ou de la navigation du contrôle WinUI du canvas. Le canvas affiche les JPEG produits par cette même capture.

En mode HQ, le navigateur remet une capture PNG au pipeline pour éviter une compression JPEG intermédiaire avant l'encodage de sortie. Le viewport web demandé reste identique ; le service de capture réalise ensuite le redimensionnement et l'encodage correspondant à la sortie.

Le host utilise le dispatcher UI existant, sans thread STA supplémentaire. Les opérations de création et de capture ont une attente bornée ; un contrôleur créé après annulation est fermé à son retour. Un stream de capture reste vivant jusqu'à la fin réelle de l'opération native, même si l'attente a expiré. L'arrêt ferme le contrôleur et détruit son HWND. Les erreurs persistantes restent observables par le service de capture.

Les sources acceptent HTTP, HTTPS et les URI `file:///`. Les images locales sont centrées avec conservation des proportions. Les vidéos locales utilisent un document interne, un mapping WebView2 vers leur répertoire et une politique CSP limitée à son média. Elles démarrent sans son et tournent en boucle. Le rafraîchissement recrée ce document. Aucun serveur HTTP ni copie temporaire du média n'est nécessaire. La disponibilité des codecs reste celle du runtime WebView2 installé.

## Vérification native effectuée

Le projet `tests/BetterSignalRGB.WebsiteHostTests` utilise le vrai contrôleur WebView2, un profil temporaire isolé et uniquement des contenus synthétiques. Il n'ouvre aucune fenêtre d'application à l'écran et ne capture pas le bureau de l'utilisateur.

Résultats validés le 27 septembre 2026 :

- JPEG à la taille du viewport demandé (320 × 240).
- Capture PNG en mode HQ avant encodage, avec la même taille de viewport.
- Pixels changeants pour `requestAnimationFrame` et animation CSS malgré l'absence de fenêtre d'application visible.
- User-agent demandé, arrêt, nouvelle session et annulation pendant l'initialisation.
- HTML animé et PNG avec espaces, `#` et accents dans le chemin local ; bandes noires et proportions de l'image vérifiées dans les pixels.
- WebM VP8 synthétique de 2,2 secondes : démarrage automatique, pixels encore changeants après la durée initiale, puis reprise après rafraîchissement automatique.
- Fichier local absent signalé avant la création du navigateur.

Ces tests vérifient le host natif indépendamment de l'éditeur. Ils ne constituent pas un test automatisé de clic sur Réduire/Paramètres dans la fenêtre WinUI complète. Les sources distantes peuvent nécessiter une authentification ou refuser la lecture ; les formats et codecs autres que ce WebM n'ont pas été validés nativement ici.

## Relancer les tests

Depuis la racine du dépôt, après génération facultative de la fixture vidéo avec le script fourni :

```powershell
node tests/GenerateLocalMediaFixture.cjs
dotnet build tests/BetterSignalRGB.WebsiteHostTests/BetterSignalRGB.WebsiteHostTests.csproj -c Release -p:Platform=x64
& 'tests/BetterSignalRGB.WebsiteHostTests/bin/x64/Release/net10.0-windows10.0.22000.0/win-x64/BetterSignalRGB.WebsiteHostTests.exe'
```

Le script de fixture utilise Playwright installé. Sans fixture, le harness annonce explicitement que le test vidéo est ignoré. La variable `BETTERSIGNALRGB_TEST_WEBM` permet de préciser le chemin d'une fixture synthétique équivalente.
