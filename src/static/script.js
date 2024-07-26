function searchSite(inputId) {
    var terms = document.getElementById(inputId).value;
    document.location.href = "https://www.google.com/search?sitesearch=originlab.com&q=" + encodeURIComponent(terms);
}

function searchDoc(lang, inputId, selectId) {
    var terms = document.getElementById(inputId).value;
    var book = document.getElementById(selectId).value;
    document.location.href = `https://www.google.com/search?sitesearch=www.originlab.com/doc/${lang}/${book}&q=` + encodeURIComponent(terms);
}