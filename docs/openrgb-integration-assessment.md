# Adaptation à OpenRGB : faisabilité et limites

Analyse du 27 septembre 2026. **Une page de sortie brute en 800 × 600 est maintenant disponible pour les consommateurs d'effets web. Aucune sortie SDK OpenRGB directe ni modification d'Effects Plugin n'est implémentée dans cette passe.** L'application conserve sa propre capture et composition. Les options ci-dessous concernent une éventuelle intégration native supplémentaire ; réutiliser tous les effets OpenRGB comme effets d'image demande un travail distinct.

## Périmètre vérifié

- OpenRGB Effects Plugin, commit [`f90f3ec5752d0d5cc09e2bb6c206a9e009f075df`](https://gitlab.com/OpenRGBDevelopers/OpenRGBEffectsPlugin/-/tree/f90f3ec5752d0d5cc09e2bb6c206a9e009f075df), lu dans un clone temporaire. L'accès web GitLab renvoyait une erreur 403 ; la consultation Git a permis de vérifier le code.
- OpenRGB, commit [`0f8f2dccc46576f0a9dac6b70adcbf49901c6b73`](https://github.com/CalcProgrammer1/OpenRGB/tree/0f8f2dccc46576f0a9dac6b70adcbf49901c6b73), documentation SDK et sérialisation des matrices vérifiées. Les liens ci-dessous sont figés sur ces versions.
- Consultation limitée, sans modification, du fork local `OpenRGB-Room`. Cette lecture sert uniquement de contexte architectural et ne constitue pas une validation matérielle.
- Le mode **HQ 800 × 600** augmente la résolution de capture, du composite web et de l'aperçu. La géométrie canonique reste **320 × 200** pour conserver exactement scènes, crops, rotations et sortie SignalRGB. Les pages `/` et `/canvas` affichent uniquement le composite, sans texte de debug. La compatibilité d'un consommateur web OpenRGB ne remplace pas une validation matérielle de bout en bout.

## Ce que fait déjà Effects Plugin

L'effet Ambient reçoit une image, applique éventuellement une région, puis adapte cette image à chaque zone. En mode copie d'écran, une zone linéaire reçoit une image redimensionnée sur une ligne ; une matrice reçoit une image redimensionnée à ses dimensions, puis une boucle parcourt ses cellules pour affecter les couleurs. Cela ne fournit pas l'éditeur de scène à plusieurs sources, recadrages et transformations de cette application. [Ambient.cpp, lignes 77–88 et 192–240](https://gitlab.com/OpenRGBDevelopers/OpenRGBEffectsPlugin/-/blob/f90f3ec5752d0d5cc09e2bb6c206a9e009f075df/Effects/Ambient/Ambient.cpp#L77-240).

La capture Windows examinée utilise GDI : allocation d'un bitmap et d'un contexte compatibles, `BitBlt`, puis conversion en image. Le gestionnaire d'effets conserve un verrou commun pendant le calcul et les mises à jour des contrôleurs ; lorsque l'aperçu est actif, il appelle aussi l'effet pour les zones d'aperçu. Ce sont des coûts à mesurer, **pas une preuve du goulot d'étranglement rencontré sur la machine de l'utilisateur**. [Capture Windows](https://gitlab.com/OpenRGBDevelopers/OpenRGBEffectsPlugin/-/blob/f90f3ec5752d0d5cc09e2bb6c206a9e009f075df/ScreenCapturer/windows/WindowsScreenCapturer.cpp#L105-151), [boucle du gestionnaire](https://gitlab.com/OpenRGBDevelopers/OpenRGBEffectsPlugin/-/blob/f90f3ec5752d0d5cc09e2bb6c206a9e009f075df/EffectManager.cpp#L339-407).

L'interface SDK du plugin examinée traite la liste, le démarrage et l'arrêt des effets. Elle n'expose pas un récepteur d'images MJPEG ou une entrée de texture pour notre application. Les effets Shaders disposent de framebuffer OpenGL, mais le chemin examiné récupère leur résultat avec `fbo->toImage()`. Réutiliser ces shaders ne garantit donc pas, à lui seul, une chaîne GPU sans copie. [Commandes réellement traitées](https://gitlab.com/OpenRGBDevelopers/OpenRGBEffectsPlugin/-/blob/f90f3ec5752d0d5cc09e2bb6c206a9e009f075df/OpenRGBEffectsPlugin.cpp#L153-180), [lecture du framebuffer](https://gitlab.com/OpenRGBDevelopers/OpenRGBEffectsPlugin/-/blob/f90f3ec5752d0d5cc09e2bb6c206a9e009f075df/Effects/Shaders/ShaderPass.cpp#L326-329).

## Résolution d'image et compteurs SDK

Le SDK standard transporte des couleurs de contrôleurs, avec négociation de version. Les nombres de LED/couleurs de la description et les compteurs des mises à jour de couleurs sont des entiers non signés de 16 bits, soit 65 535 au maximum. Cela limite la représentation d'un écran comme un unique contrôleur dense où chaque pixel serait une LED ; cela ne limite pas une texture ou une image indépendante à 65 535 pixels. [Description et mises à jour SDK](https://github.com/CalcProgrammer1/OpenRGB/blob/0f8f2dccc46576f0a9dac6b70adcbf49901c6b73/Documentation/OpenRGBSDK.md#net_packet_id_rgbcontroller_updateleds).

Une autre limite concerne le descripteur de matrice : sa longueur est annoncée sur 16 bits, pour un bloc contenant 8 octets de dimensions et 4 octets par cellule. Une longueur fidèle tient donc jusqu'à 16 381 cellules. Le parseur C++ examiné utilise surtout cette longueur comme indicateur de présence, puis lit la carte selon ses dimensions ; certaines matrices plus grandes peuvent passer malgré une longueur tronquée. Elles ne constituent pas une représentation interopérable fiable. [Calcul du bloc](https://github.com/CalcProgrammer1/OpenRGB/blob/0f8f2dccc46576f0a9dac6b70adcbf49901c6b73/RGBController/RGBController.cpp#L2794-2813), [écriture de la longueur](https://github.com/CalcProgrammer1/OpenRGB/blob/0f8f2dccc46576f0a9dac6b70adcbf49901c6b73/RGBController/RGBController.cpp#L3176-3184), [lecture](https://github.com/CalcProgrammer1/OpenRGB/blob/0f8f2dccc46576f0a9dac6b70adcbf49901c6b73/RGBController/RGBController.cpp#L3861-3877).

| Image ou matrice dense | Pixels/cellules | Compteur de couleurs 16 bits | Longueur fidèle du bloc de matrice |
|---|---:|---|---|
| 80 × 50 | 4 000 | Oui | Oui |
| 160 × 100 | 16 000 | Oui | Oui |
| 320 × 200 | 64 000 | Oui | Non |
| 800 × 600 | 480 000 | Non | Non |

Ces calculs ne sont pas des benchmarks. Une image 800 × 600 peut alimenter, par exemple, 1 000 LED physiques en ne transmettant que leurs 1 000 couleurs échantillonnées. Même une carte creuse très étendue a un coût si l'effet parcourt toutes ses cellules ; échantillonner uniquement les positions utiles évite ce travail.

## Options concrètes

| Option | Résultat | Travail et difficulté qualitative |
|---|---|---|
| **Sortie SDK dans notre application** | Le composite pilote directement les appareils OpenRGB. | Difficulté modérée : connexion/version, inventaire et reconnexion, mode direct, correspondance persistante des appareils et LED, échantillonnage et cadence par appareil. Ne réutilise pas automatiquement le catalogue Effects Plugin. |
| **Nouvel effet StreamInput dans Effects Plugin** | Le plugin reçoit notre composite et l'applique aux zones. | Difficulté intermédiaire : récepteur MJPEG ou mémoire partagée, décodage, reconnexion et tampon borné à la dernière image. MJPEG réutilise le flux actuel, mais ajoute compression/décompression. Les grandes matrices restent coûteuses si les boucles actuelles sont conservées. |
| **Moteur d'images partagé avec le catalogue d'effets** | Effets et capture produisent une surface de résolution configurable pour LED, wallpaper et autres écrans. | Difficulté élevée : interface de rendu d'image/texture, adaptation des effets calculés par LED, cohérence des coordonnées et du temps, composition, synchronisation et sorties. Le partage OpenGL/Direct3D demande une conception explicite. |

Pour le fork existant, conserver l'export de coordonnées LED et compléter ses correspondances matérielles est préférable à refaire le placement. Le projet wallpaper identifié dans son audit possède déjà un serveur OpenRGB exposant des matrices virtuelles ; cette voie de compatibilité existe donc avant tout nouveau fork. Son code reste soumis aux contraintes des descripteurs et compteurs. [Serveur wallpaper, commit `2d1099eeaf1f59c1693e22503ba95d7a50fc6603`](https://github.com/Delido/signalrgb-wallpaper/blob/2d1099eeaf1f59c1693e22503ba95d7a50fc6603/wallpaper_bridge/openrgb_server.py).

## Architecture conseillée et validation

```text
Sources + effets → composite d'image configurable
                    ├─ positions des LED → couleurs → SDK OpenRGB → matériel
                    ├─ image/texture complète → wallpaper
                    └─ matrice réduite → compatibilité des effets existants
```

Commencer par un contrat de scène commun et une résolution configurable, puis une sortie SDK et un transport d'images séparé. Pour ce transport, définir largeur, hauteur, format, stride, numéro de trame et capacité négociée ; conserver uniquement la dernière trame disponible. Une mémoire partagée constitue une première option locale ; le partage de textures devient pertinent si les mesures montrent que les copies dominent. Ne pas élargir silencieusement les champs du SDK existant.

Comparer la même scène en 320 × 200, 800 × 600 et à la résolution cible : temps de capture, composition, lecture GPU, encodage, transfert, calcul des effets et envoi au matériel ; mesurer aussi CPU, GPU, mémoire, images abandonnées et latence. Tester arrêt/reprise, redétection des appareils, navigation, fermeture et consommateurs lents. Aucun gain chiffré ni délai de livraison n'est déduit de cette seule lecture de code.
