// Declenche le telechargement navigateur d'un fichier recu en bytes depuis l'API
// (utilise par Pages/Ocr.razor pour l'export Excel, cote client uniquement).
window.downloadFileFromBytes = (fileName, contentType, base64Content) => {
    const link = document.createElement("a");
    link.href = `data:${contentType};base64,${base64Content}`;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};
