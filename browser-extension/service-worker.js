"use strict";

var HOST = "pl.sirk.agent.browser";

function send(value) {
  value.timestampUtc = new Date().toISOString();
  try {
    chrome.runtime.sendNativeMessage(HOST, value, function () {
      void chrome.runtime.lastError;
    });
  } catch (_) {}
}

chrome.tabs.onActivated.addListener(function (info) {
  chrome.tabs.get(info.tabId, function (tab) {
    if (chrome.runtime.lastError || !tab) return;
    send({ type: "tab", url: tab.url || "", title: tab.title || "" });
  });
});

chrome.webNavigation.onCommitted.addListener(function (details) {
  if (details.frameId !== 0) return;
  send({ type: "navigation", url: details.url || "", transitionType: details.transitionType || "" });
});

chrome.downloads.onCreated.addListener(function (item) {
  var name = String(item.filename || "").split(/[\\/]/).pop() || "";
  send({ type: "download", url: item.finalUrl || item.url || "", fileName: name,
    mime: item.mime || "", bytes: Number(item.totalBytes || 0) });
});

chrome.runtime.onMessage.addListener(function (message) {
  if (!message || message.source !== "sirk-content") return;
  send(message.event || {});
});

chrome.webRequest.onCompleted.addListener(function (details) {
  if (!["POST", "PUT", "PATCH"].includes(details.method)) return;
  send({ type: "uploadResult", url: details.url || "", requestId: details.requestId || "",
    method: details.method, statusCode: Number(details.statusCode || 0), ok: details.statusCode < 400 });
}, { urls: ["http://*/*", "https://*/*"] });

chrome.webRequest.onErrorOccurred.addListener(function (details) {
  if (!["POST", "PUT", "PATCH"].includes(details.method)) return;
  send({ type: "uploadResult", url: details.url || "", requestId: details.requestId || "",
    method: details.method, ok: false, error: details.error || "request failed" });
}, { urls: ["http://*/*", "https://*/*"] });
