function searchSite(inputId) {
    var terms = document.getElementById(inputId).value;
    document.location.href = "https://www.google.com/search?sitesearch=www.originlab.com&q=" + encodeURIComponent(terms);
}

function searchDoc(inputId, lang, book) {
    var terms = document.getElementById(inputId).value;
    document.location.href = `https://www.google.com/search?sitesearch=www.originlab.com/doc/${lang}/${book}&` + encodeURIComponent(terms);
}