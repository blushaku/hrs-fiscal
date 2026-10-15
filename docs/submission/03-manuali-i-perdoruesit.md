# Manuali i përdoruesit — Opera Cloud Fiscal Solution - Kosovo

Behar Lushaku · 12.10.2026

## 1. Hyrje

Opera Cloud Fiscal Solution - Kosovo fiskalizon automatikisht çdo faturë që mbyllet në Oracle OPERA Cloud. Recepsionisti punon si zakonisht në OPERA; softueri fiskal punon në sfond dhe administrohet përmes shfletuesit web në adresën e serverit të hotelit (p.sh. `http://<IP-e-serverit>:5080`).

Të drejtat varen nga roli i përdoruesit. Administratori mund t'i ndryshojë të drejtat e roleve te **Cilësimet › Rolet dhe të drejtat**.

| Roli | Çka sheh dhe çka bën (parazgjedhur) |
| --- | --- |
| Administrator | Të gjitha: paneli, kuponët, anulimi, kuponët nga blloku, auditimi, eksportet, mesazhet FLIP, të gjitha cilësimet, përdoruesit |
| Mbikëqyrës | Paneli i plotë, kuponët dhe kopjet, anulimi i kuponëve, shënimi i kuponëve nga blloku, eksportet, auditimi dhe kontrolli i integritetit, mesazhet FLIP |
| Arkëtar | Kërkimi i kuponëve dhe kopja e kuponit |
| Auditor | Kuponët, eksportet, auditimi dhe kontrolli i integritetit (pa ndryshime) |

**Gjuha.** Ndërfaqja është në shqip; me **SQ · SR · EN** në krye të faqes (edhe në faqen e hyrjes) zgjidhet shqip, serbisht (alfabet latin) ose anglisht, dhe zgjedhja ruhet për shfletuesin. Emrat e ekraneve dhe butonave në këtë manual janë ashtu siç shfaqen në ndërfaqen shqip.

## 2. Puna e përditshme në recepsion

**Mbyllja e faturës.** Mbyllni (paguani) faturën në OPERA si zakonisht. Brenda pak sekondave softueri e fiskalizon dhe OPERA shtyp faturën me bllokun fiskal:

- kodi QR (mysafiri mund ta verifikojë me aplikacionin e ATK-së);
- numri i kuponit, numri ditor ("KUPON FISKAL DITOR NR.") dhe NUIKF (numri i verifikimit);
- ID e SEF-it (njësia–NUI–POS) dhe ID e transaksionit në ATK;
- data dhe ora, teksti "e-kupon" dhe, kur ATK-ja nuk arrihet, shënimi OFFLINE.

**Fatura refuzohet.** Nëse OPERA tregon mesazh gabimi nga softueri fiskal, kuponi nuk është lëshuar. Mesazhi tregon arsyen, p.sh. shuma që nuk përputhen ose normë TVSH e pakonfiguruar. Korrigjoni faturën në OPERA dhe mbylleni sërish; nëse arsyeja është në konfigurim, njoftoni administratorin.

**Pa lidhje me ATK-në (OFFLINE).** Puna vazhdon normalisht: kuponi lëshohet dhe fatura shtypet me shënimin OFFLINE. Softueri e dërgon kuponin automatikisht sapo rikthehet lidhja. Nuk nevojitet asnjë veprim nga recepsioni.

**Kuponi në printer termik.** Nëse hoteli e ka zgjedhur këtë mënyrë (Cilësimet › Printimi), menjëherë pas fiskalizimit kuponi i plotë shtypet në printerin termik të vendpunësit (58 ose 80 mm), me artikujt, TVSH-në, totalet, NUIKF, numrin ditor, kodin QR dhe "e-kupon". Nëse printeri nuk punon, kuponi mbetet i fiskalizuar: paneli tregon alarm dhe kuponi shtypet nga faqja e tij (**Shtyp në printerin termik**) pasi të rregullohet printeri. Çdo shtypje e mëvonshme shënohet "KOPJE E KUPONIT".

**Rishtypja.** Rishtypja e faturës në OPERA nuk krijon kupon të ri; shtypen të njëjtat të dhëna fiskale. Kopja e kuponit nga softueri fiskal shënohet "KOPJE E KUPONIT".

**Korrigjimi i një fature të fiskalizuar.** Kuponi i lëshuar nuk mund të ndryshohet ose fshihet. Bëni korrigjimin në OPERA (shumë negative ose faturë kreditore që i referohet faturës origjinale): softueri lëshon **kupon kthimi** që i referohet kuponit origjinal.

**Anulimi i kuponit.** Për një gabim të vërejtur pas lëshimit (p.sh. gabim gjatë shtypjes), mbikëqyrësi ose administratori hap kuponin te **Kuponët**, zgjedh **Anulo kuponin…** dhe shënon arsyen. Softueri lëshon **kupon anulimi** ("KUPON FISKAL I ANULUAR") me të njëjtat rreshta dhe shuma, me arsyen e anulimit, të lidhur me kuponin origjinal, dhe e dërgon në ATK. Një kupon anulohet vetëm një herë; për të kthyer vetëm një pjesë të shumës përdoret kthimi në OPERA.

**Kur sistemi nuk punon fare.** Nëse kompjuteri ose serveri fiskal ndalon plotësisht, kuponët shkruhen me dorë nga blloku i veçantë i kuponëve fiskalë. Brenda 48 orëve sistemi duhet të vihet në punë; brenda 5 ditëve pas kësaj, çdo kupon i bllokut shënohet te **Kuponët › Shëno kupon nga blloku**: vendpunësi, numri serik i kuponit, data dhe ora e shkruar në të, kush e shkroi, rreshtat dhe pagesa. Softueri e nënshkruan dhe e dërgon në ATK me numrin serik dhe kohën e letrës.

## 3. Hyrja në sistem dhe paneli

**Hyrja.** Hapni adresën e serverit në shfletues, shkruani përdoruesin dhe fjalëkalimin dhe shtypni **Hyr**. Dilni me **Dil** në shiritin e sipërm. Çdo hyrje regjistrohet në auditim.

**Paneli** tregon gjendjen e ditës; me **Rifresko** rifreskohet:

- rrjedha OPERA → FLIP → serveri fiskal → ATK me gjendjen e secilës pjesë;
- kuponet e lëshuara sot dhe shuma totale, të pranuara nga ATK-ja, në pritje për dërgim (me afatin 48-orësh) dhe të refuzuara;
- vendpunësit (POS): kuponi i fundit, kuponet në pritje, skadimi i certifikatës;
- alarmet: kuponë të padërguar (afati 48-orësh dhe data 10 e muajit pasues), certifikata që skadojnë, ora e serverit që ndryshon nga ajo e ATK-së, mungesa e normave të TVSH-së.

Arkatari sheh vetëm kërkimin e kuponit.

## 4. Kuponet, mesazhet FLIP, auditimi dhe eksportet

**Kuponët.** Kërkoni sipas numrit të kuponit, NUIKF, faturës ose arkëtarit, me periudhë (**Nga / Deri**), vendpunës dhe lloj (Shitje, Kthim, Anulim); filtroni sipas statusit: Të gjitha, Pranuar, Në pritje të dërgimit, Refuzuar. **Hap** tregon detajet: artikujt, TVSH-ja, pagesat, numri ditor, kodi QR, çdo dërgim në ATK me përgjigjen dhe mesazhi origjinal nga OPERA. **Shkarko kopjen (PDF)** krijon kopje në formatin e kuponit (80 mm) të shënuar "KOPJE E KUPONIT" (regjistrohet si rishtypje).

**Mesazhet FLIP.** Çdo mesazh që OPERA dërgon ruhet ashtu siç është pranuar, bashkë me përgjigjen e softuerit dhe lidhjen me kuponin. Përdoret për kontroll dhe për zgjidhjen e problemeve.

**Regjistri i auditimit.** Lista e të gjitha veprimeve me datë, përdorues, veprim dhe detaje; filtrohet sipas periudhës, veprimit, përdoruesit dhe vendpunësit. **Kontrollo integritetin** verifikon zinxhirin e hash-eve të kuponëve dhe auditimit: rezultati duhet të jetë "Integriteti u verifikua".

**Eksporti.** Zgjidhni të dhënat (kuponët, kuponët ende të padërguar në ATK, përmbledhja sipas vendpunësit, regjistri i auditimit), periudhën, vendpunësin dhe formatin CSV ose PDF. Eksporti regjistrohet në auditim me hash-in e skedarit. Me kërkesë të ATK-së ose të autoritetit tjetër shtetëror, eksportoni kuponët ende të padërguar.

## 5. Cilësimet (administratori)

Të gjitha cilësimet janë te **Cilësimet**. Çdo pllakë hapet me **Ndrysho** në dritare të veçantë; para ruajtjes softueri tregon ndryshimet (vlera e vjetër → e re) dhe kërkon konfirmim. Çdo ndryshim regjistrohet në auditim.

**Konfigurimi i parë, sipas radhës:**

1. **Biznesi dhe njësia** — hapi i parë. Në pjesën **1 Lëshuar nga ATK** shënoni NUI-n, numrin e fiskalizimit (nga EDI), numrin e njësisë në ATK dhe ID-në e aplikacionit (lëshuar nga ATK-ja me certifikimin), pastaj shtypni **Valido në ATK**. Kur ATK-ja i konfirmon vlerat, hapet pjesa **2 Të dhënat e biznesit dhe njësisë**, me emrin e regjistruar të marrë nga përgjigjja e ATK-së: plotësoni numrin e TVSH-së, emrin e njësisë, qytetin, adresën dhe kodin e pronës në OPERA dhe ruani. Nëse ATK-ja nuk arrihet dhe vlerat janë të sakta, **Plotëso të dhënat gjithsesi**. Të gjitha vlerat mund të ndryshohen më vonë; ndryshimi i NUI-t ose i numrit të njësisë e ndërron tatimpaguesin (kuponët e lëshuar mbeten nën tatimpaguesin e mëparshëm dhe vendpunët regjistrohen përsëri). Rreshti **Emri në ATK** tregon nëse emri përputhet me ATK-në; me **Përdor emrin e ATK-së** merret emri i saktë. **Fillo nga e para me biznes të ri** fshin të gjitha të dhënat vetëm kur serveri nuk ka punuar kurrë me Prodhimin e ATK-së.
2. **Të përgjithshme › Lidhja me ATK-në** — mjedisi (Test / Prodhim), afati i pritjes dhe ridërgimi. Prodhimi zgjidhet vetëm për punë reale.
3. **Vendpunët** — çdo vendpunës që lëshon fatura në OPERA: ID e POS-it, ID e terminalit fiskal në OPERA, emri i kompjuterit. **Shto vendpunës** e shton dhe e regjistron menjëherë në ATK (**Regjistro në ATK tani**) ose importon çelësin dhe certifikatën nga mjeti i ATK-së (**Importo nga mjeti onboarder i ATK-së**). Gjendja **Gati** do të thotë që POS-i mund të fiskalizojë. Një vendpunës që nuk ka lëshuar asnjë kupon mund të fshihet (**Ndrysho › Fshi vendpunësin**); një vendpunës me kuponë nuk fshihet, sepse kuponët e tij ruhen: vendosni statusin *çaktivizuar*. Për një vendpunës me nënshkrim **Klienti në vendpunës**: te pjesa **Fiscal Client** shtypni **Krijo paketën e instalimit**, pastaj **Shkarko paketën e instalimit** dhe në atë kompjuter ekzekutoni `install-client.ps1` si administrator (ose instaloni klientin standard me adresën e serverit dhe kodin e çiftimit që tregohet). Kodi vlen një herë, 2 ditë. Brenda një minute klienti lidhet me serverin, krijon çelësin e vendpunësit dhe regjistrohet në ATK; gjendja shfaqet si *në linjë / jashtë linje*. Në vendpunës, pranë orës së Windows shfaqet një pikë: **e gjelbër** ATK në linjë, **portokalli** kuponët po dërgohen, **e kuqe** problem. Kur ATK-ja nuk arrihet, hapet një dritare e vogël në këndin e ekranit me njoftimin **Pa lidhje me ATK-në**, që nga kur, numrin e kuponëve në pritje dhe afatin 48 orë; kuponët lëshohen normalisht, shënohen OFFLINE dhe dërgohen automatikisht. Dritarja nuk mbyllet derisa të kthehet lidhja (**Fshihe** e largon për 10 minuta).
4. **Zëvendësimet e OPERA-s dhe TVSH** — lista e normave të TVSH-së fillon bosh: me **Shto normë të TVSH-së** shtoni çdo normë që përdor hoteli, me shkronjën e ATK-së (A e përjashtuar, C 0 %, D e reduktuar, E standarde), përqindjen dhe përshkrimin. Për çdo përqindje, norma e shënuar **Përdore për rreshtat e OPERA-s me këtë përqindje** përdoret për rreshtat e faturës. Pa norma të TVSH-së asnjë faturë nuk fiskalizohet. Sipas nevojës: kodet e transaksioneve dhe mënyrat e pagesës.
5. **Të përgjithshme › Lidhja OPERA / FLIP** — mënyra e punës (Regjistrim për testim, Live për fiskalizim) dhe tokeni i qasjes për FLIP (**Gjenero tokenin**), i cili tregohet vetëm një herë.
6. **Të përgjithshme › Validimi i faturës** — kontrolli i numrit tatimor dhe toleranca e rrumbullakimit.
7. **Përdoruesit** dhe **Rolet dhe të drejtat** — llogari personale për çdo përdorues; përdoruesit nuk fshihen, çaktivizohen.
8. **Printimi** — ku shtypet kuponi për mysafirin: në bllokun fiskal të faturës së OPERA-s (parazgjedhje) ose në printerin termik të vendpunësit. Për çdo vendpunës, me **Konfiguro**: lidhja (rrjet me adresën IP dhe portën 9100; USB ose portë seriale COM në serverin fiskal, me shpejtësinë dhe kontrollin e rrjedhës; ose USB / COM në vetë vendpunësin, kur ai ka Fiscal Client), modeli i printerit (p.sh. Epson TM-T88VII, 80 mm, 42 shkronja për rresht) dhe shtypja automatike pas fiskalizimit. **Provë printimi** shtyp një faqe prove (jo kupon fiskal).

Pllakat e tjera: **Nënshkrimi** (ku ruhen çelësat: qendror në server, parazgjedhje, ose klienti në vendpunës me Fiscal Client), **Zëvendësimet e OPERA-s dhe vlerat e parazgjedhura të kuponit**, **Llogaritja e TVSH-së**, **Ruajtja dhe kopja rezervë**, **Alarmet** (adresa për njoftime). Ora e serverit kontrollohet automatikisht me ATK-në çdo 15 minuta.

## 6. Situata të veçanta dhe zgjidhja e problemeve

| Situata | Çka bën softueri | Çka bëni ju |
| --- | --- | --- |
| Nuk ka lidhje me ATK-në | Lëshon kuponin, e ruan dhe e dërgon automatikisht kur rikthehet lidhja | Asgjë në recepsion. Administratori ndjek kuponet në pritje në panel |
| Kupone të padërguara afro 48 orë | Shfaq alarm në panel dhe njofton administratorin | Kontrolloni internetin e serverit; nëse ndërprerja zgjat mbi 48 orë, njoftoni ATK-në |
| Fatura refuzohet | Nuk lëshon kupon; tregon arsyet; regjistron refuzimin | Korrigjoni faturën në OPERA ose konfigurimin (TVSH, kodi i hotelit) dhe mbylleni përsëri |
| ATK-ja refuzon kuponin | Ruan përgjigjen e ATK-së te kuponi; shfaqet si Refuzuar | Administratori shqyrton arsyen te detajet e kuponit dhe kontakton mirëmbajtësin |
| Certifikata e POS-it skadon | Paralajmëron në panel para skadimit | Regjistroni POS-in përsëri te **Vendpunët** |
| Serveri fiskal nuk punon | OPERA nuk merr përgjigje; fatura nuk fiskalizohet | Rinisni shërbimin "Opera Cloud Fiscal Solution - Kosovo - Server"; nëse mungon gjatë, përdorni procedurën e ATK-së për pajisje jashtë funksionit dhe kontaktoni mirëmbajtësin |
| Kuponi nuk shtypet në printerin termik | Kuponi fiskalizohet dhe dërgohet në ATK; paneli tregon alarm dhe dështimi regjistrohet në auditim | Kontrolloni printerin (i ndezur, letër, rrjeti) dhe bëni Provë printimi te Cilësimet › Printimi; pastaj shtypeni kuponin nga faqja e tij |
| Kontrolli i integritetit dështon | Tregon "Kontrolli i integritetit DËSHTOI" | Mos bëni ndryshime; njoftoni menjëherë mirëmbajtësin |

**Zhvilluesi dhe mirëmbajtësi:** Behar Lushaku · tel. \_\_\_\_\_\_\_\_\_\_\_\_\_\_ · e-mail \_\_\_\_\_\_\_\_\_\_\_\_\_\_

## 7. Instalimi dhe kopjet rezervë

Instalimi bëhet nga mirëmbajtësi sipas **Manualit të instalimit dhe të konfigurimit** (shtojcë): kërkesat e serverit (Windows, .NET 8, PostgreSQL 16), instalimi si shërbim Windows, firewall-i, lidhja me OPERA FLIP dhe lista e kontrollit para përdorimit.

Baza e të dhënave është arkivi ligjor i kuponeve: kopja rezervë bëhet çdo ditë në disk tjetër ose NAS, me kontroll të saktësisë dhe njoftim në regjistrin e ngjarjeve të Windows. Të dhënat nuk fshihen kurrë, as gjatë çinstalimit të softuerit.
