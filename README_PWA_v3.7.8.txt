AJOJÄRJESTYS v3.7.9 – PWA / Chromebook
Tekijä: Teemu H. Fingerroos

Tämä PWA-versio on päivitetty Windows v3.7.7 -version toimivan osoite- ja vastaanottajatunnistuksen pohjalta.

Tärkeä muutos:
- PDF:n tekstikenttiä käsitellään PDF.js:n koordinaattien avulla.
- Täsmällinen "Vastaanottaja:"-otsikko toimii ensisijaisena ankkurina.
- Osoite valitaan vastaanottajalohkon sisältä, ei koko PDF:stä.
- Lähettäjän osoitteen päätyminen vastaanottajan osoitteeksi on näin paljon epätodennäköisempää.
- Osoitenumerot kuten 13-19, 12 A ja vastaavat ovat huomioitu selainlogiikassa.
- Epävarma/puuttuva osoite avaa PDF:n vastaanottaja-alueen esikatselun.

Käyttö:
1. Julkaise kansio HTTPS-osoitteeseen GitHub Pagesilla.
2. Avaa AjoJärjestys Chromebookin Chromella.
3. Lisää PDF:t valitsemalla tiedostot tai raahaamalla ne ohjelmaan.
4. Tarkista epävarmat rivit popupista.
5. Muokkaa tarvittaessa vastaanottajaa tai osoitetta ja paina Hyväksy.

Huomio:
- PDF käsitellään selaimessa. Tätä versiota varten PDF.js ladataan cdnjs-palvelusta.
- Tämä on selainportti Windows-version tunnistuslogiikasta; Windowsin PdfPig-kirjastoa ei käytetä selaimessa.
