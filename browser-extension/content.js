"use strict";

function fileMetadata(files) {
  return Array.prototype.slice.call(files || [], 0, 100).map(function (file) {
    var name = String(file.name || "");
    var dot = name.lastIndexOf(".");
    return { name: name, extension: dot >= 0 ? name.slice(dot).toLowerCase() : "",
      bytes: Number(file.size || 0), mime: String(file.type || "") };
  });
}

document.addEventListener("change", function (event) {
  var input = event.target;
  if (!input || input.tagName !== "INPUT" || input.type !== "file") return;
  chrome.runtime.sendMessage({ source: "sirk-content", event: {
    type: "uploadSelection", url: location.href, files: fileMetadata(input.files)
  } });
}, true);

document.addEventListener("drop", function (event) {
  var files = event.dataTransfer && event.dataTransfer.files;
  if (!files || !files.length) return;
  chrome.runtime.sendMessage({ source: "sirk-content", event: {
    type: "dragDrop", url: location.href, files: fileMetadata(files)
  } });
}, true);

document.addEventListener("submit", function () {
  chrome.runtime.sendMessage({ source: "sirk-content", event: {
    type: "formSubmit", url: location.href
  } });
}, true);
