import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";

const file = "Jellyfin.Plugin.Hue/Configuration/configPage.html";
const html = fs.readFileSync(file, "utf8");
const scriptMatch = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);

if (!scriptMatch) {
    throw new Error(`${file} does not contain the configuration script`);
}

const exportCases = [
    {
        method: "exportConfiguration",
        key: "configurationExport",
        flag: "_hueConfigurationExporting",
        button: "#exportConfigurationBtn",
        route: "HueSync/Configuration/Export",
        fileName: "jellyfin-hue-configuration.json",
        dataType: "json",
        contentType: "application/json",
        response: { UserMappings: [], Marker: "configuration" }
    },
    {
        method: "exportSceneScheduleConflicts",
        key: "sceneScheduleConflictsExport",
        flag: "_hueSceneScheduleConflictsExporting",
        button: "#exportSceneScheduleConflictsBtn",
        route: "HueSync/SceneSchedules/Conflicts?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-schedule-conflicts.json",
        dataType: "json",
        contentType: "application/json",
        response: { Conflicts: [], Marker: "conflicts" }
    },
    {
        method: "exportSceneScheduleConflictsCsv",
        key: "sceneScheduleConflictsCsvExport",
        flag: "_hueSceneScheduleConflictsCsvExporting",
        button: "#exportSceneScheduleConflictsCsvBtn",
        route: "HueSync/SceneSchedules/Conflicts/ExportCsv?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-schedule-conflicts.csv",
        dataType: "text",
        contentType: "text/csv;charset=utf-8",
        response: "kind,marker\nconflicts,csv"
    },
    {
        method: "exportSceneScheduleOccurrences",
        key: "sceneScheduleOccurrencesExport",
        flag: "_hueSceneScheduleOccurrencesExporting",
        button: "#exportSceneScheduleOccurrencesBtn",
        route: "HueSync/SceneSchedules/Occurrences?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-schedule-occurrences.json",
        dataType: "json",
        contentType: "application/json",
        response: { Occurrences: [], Marker: "occurrences" }
    },
    {
        method: "exportSceneScheduleOccurrencesCsv",
        key: "sceneScheduleOccurrencesCsvExport",
        flag: "_hueSceneScheduleOccurrencesCsvExporting",
        button: "#exportSceneScheduleOccurrencesCsvBtn",
        route: "HueSync/SceneSchedules/Occurrences/ExportCsv?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-schedule-occurrences.csv",
        dataType: "text",
        contentType: "text/csv;charset=utf-8",
        response: "kind,marker\noccurrences,csv"
    },
    {
        method: "exportSceneScheduleCalendar",
        key: "sceneScheduleCalendarExport",
        flag: "_hueSceneScheduleCalendarExporting",
        button: "#exportSceneScheduleCalendarBtn",
        route: "HueSync/SceneSchedules/Calendar?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-cues.ics",
        dataType: "text",
        contentType: "text/calendar;charset=utf-8",
        response: "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n"
    },
    {
        method: "exportSceneScheduleHistory",
        key: "sceneScheduleHistoryExport",
        flag: "_hueSceneScheduleHistoryExporting",
        button: "#exportSceneScheduleHistoryBtn",
        route: "HueSync/SceneSchedules/History/Export?limit=100&scheduleId=cue-1&outcome=Error",
        fileName: "jellyfin-hue-scene-schedule-history.json",
        dataType: "json",
        contentType: "application/json",
        response: { History: [], Marker: "schedule-history" }
    },
    {
        method: "exportSceneScheduleHistoryCsv",
        key: "sceneScheduleHistoryCsvExport",
        flag: "_hueSceneScheduleHistoryCsvExporting",
        button: "#exportSceneScheduleHistoryCsvBtn",
        route: "HueSync/SceneSchedules/History/ExportCsv?limit=100&scheduleId=cue-1&outcome=Error",
        fileName: "jellyfin-hue-scene-schedule-history.csv",
        dataType: "text",
        contentType: "text/csv;charset=utf-8",
        response: "kind,marker\nschedule-history,csv"
    },
    {
        method: "exportSessionHistory",
        key: "sessionHistoryExport",
        flag: "_hueSessionHistoryExporting",
        button: "#exportSessionHistoryBtn",
        route: "HueSync/History/Export?limit=25&outcome=Stopped",
        fileName: "jellyfin-hue-session-history.json",
        dataType: "json",
        contentType: "application/json",
        response: { Sessions: [], Marker: "session-history" }
    },
    {
        method: "exportSessionHistoryCsv",
        key: "sessionHistoryCsvExport",
        flag: "_hueSessionHistoryCsvExporting",
        button: "#exportSessionHistoryCsvBtn",
        route: "HueSync/History/ExportCsv?limit=25&outcome=Stopped",
        fileName: "jellyfin-hue-session-history.csv",
        dataType: "text",
        contentType: "text/csv;charset=utf-8",
        response: "kind,marker\nsession-history,csv"
    }
];

function makeElement(tagName = "div") {
    const element = {
        tagName: String(tagName).toUpperCase(),
        disabled: false,
        value: "",
        textContent: "",
        selectedIndex: 0,
        options: [],
        dataset: {},
        attributes: {},
        style: {},
        children: [],
        listeners: {},
        classList: {
            contains: () => false,
            add: () => {},
            remove: () => {}
        },
        addEventListener(eventName, handler) {
            this.listeners[eventName] = handler;
        },
        setAttribute(name, value) {
            this.attributes[name] = String(value);
        },
        getAttribute(name) {
            return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null;
        },
        appendChild(child) {
            this.children.push(child);
            if (this.tagName === "SELECT" && child && child.tagName === "OPTION") {
                this.options.push(child);
                if (this.options.length === 1) {
                    this.value = child.value;
                    this.selectedIndex = 0;
                }
            }
            return child;
        },
        removeChild(child) {
            const index = this.children.indexOf(child);
            if (index >= 0) this.children.splice(index, 1);
            return child;
        },
        click() {},
        querySelector() {
            return makeElement();
        },
        closest() {
            return null;
        }
    };
    return element;
}

function makeHarness() {
    const downloads = [];
    const blobs = new Map();
    const requests = [];
    const readers = [];
    let blobNumber = 0;
    const pageElements = new Map();
    let activePage = null;

    const body = makeElement("body");
    const document = {
        body,
        querySelector(selector) {
            if (selector === '.pluginConfigurationPage' && activePage) return activePage;
            return makeElement();
        },
        getElementById(id) {
            if (!pageElements.has(id)) {
                pageElements.set(id, makeElement());
            }
            return pageElements.get(id);
        },
        querySelectorAll() {
            return [];
        },
        createTextNode(value) {
            return { textContent: String(value || "") };
        },
        createElement(tagName) {
            const element = makeElement(tagName);
            if (String(tagName).toLowerCase() === "a") {
                element.click = () => {
                    downloads.push({
                        fileName: element.download,
                        href: element.href,
                        blob: blobs.get(element.href)
                    });
                };
            }
            return element;
        },
        addEventListener() {}
    };

    const url = {
        createObjectURL(blob) {
            const value = `blob:hue-config-contract-${++blobNumber}`;
            blobs.set(value, blob);
            return value;
        },
        revokeObjectURL(value) {
            blobs.delete(value);
        }
    };

    class TestBlob {
        constructor(parts, options) {
            this.parts = parts;
            this.type = options && options.type;
        }
    }

    class TestFileReader {
        constructor() {
            this.result = "";
            this.error = null;
            this.onload = null;
            this.onerror = null;
            this.aborted = false;
            readers.push(this);
        }

        readAsText(file) {
            this.file = file;
        }

        abort() {
            this.aborted = true;
        }

        resolve(result) {
            this.result = result;
            if (typeof this.onload === "function") this.onload();
        }

        reject(error) {
            this.error = error;
            if (typeof this.onerror === "function") this.onerror();
        }
    }

    const dashboard = {
        alerts: [],
        hideLoadingMsg() {},
        showLoadingMsg() {},
        confirm() {},
        alert(message) {
            this.alerts.push(message);
        }
    };

    function makeDeferred(options) {
        let resolvePromise;
        let rejectPromise;
        const promise = new Promise((resolve, reject) => {
            resolvePromise = resolve;
            rejectPromise = reject;
        });
        promise.options = options;
        promise.aborted = false;
        promise.abort = () => {
            promise.aborted = true;
        };
        return {
            promise,
            options,
            resolve: resolvePromise,
            reject: rejectPromise
        };
    }

    const apiClient = {
        getUrl(value) {
            return value;
        },
        ajax(options) {
            const deferred = makeDeferred(options);
            requests.push(deferred);
            return deferred.promise;
        }
    };

    const context = vm.createContext({
        ApiClient: apiClient,
        Blob: TestBlob,
        FileReader: TestFileReader,
        Dashboard: dashboard,
        URL: url,
        console: {
            error() {},
            warn() {},
            log() {}
        },
        document,
        window: {
            document,
            URL: url,
            setTimeout(callback) {
                callback();
                return 1;
            },
            clearTimeout() {}
        },
        setTimeout(callback) {
            callback();
            return 1;
        },
        clearTimeout() {},
        AbortController
    });

    new vm.Script(`"use strict";\n${scriptMatch[1]}`, { filename: file }).runInContext(context);

    const page = {
        _huePageActive: true,
        _huePageGeneration: 7,
        querySelector(selector) {
            if (typeof selector !== "string" || selector[0] !== "#") return null;
            const id = selector.slice(1);
            if (!pageElements.has(id)) {
                pageElements.set(id, makeElement());
            }
            return pageElements.get(id);
        },
        querySelectorAll() {
            return [];
        }
    };
    activePage = page;

    for (const [id, value] of [
        ["sceneScheduleConflictFilter", "cue-1"],
        ["sceneScheduleOccurrenceFilter", "cue-1"],
        ["sceneScheduleReportHorizon", "31"],
        ["sceneScheduleHistoryOutcome", "Error"],
        ["sceneScheduleHistoryCueFilter", "cue-1"],
        ["sessionHistoryOutcome", "Stopped"]
    ]) {
        const element = page.querySelector(`#${id}`);
        element.value = value;
        element.selectedIndex = 1;
        element.options = [
            { value: "", textContent: "All cues" },
            { value, textContent: "Cue One" }
        ];
    }

    return {
        page,
        api: context.HueConfigurationPage,
        requests,
        readers,
        downloads,
        blobs,
        dashboard
    };
}

function mutateQuery(page, method) {
    if (method.includes("Conflicts")) {
        page.querySelector("#sceneScheduleConflictFilter").value = "cue-2";
    } else if (method.includes("Occurrences") || method.includes("Calendar")) {
        page.querySelector("#sceneScheduleOccurrenceFilter").value = "cue-2";
    } else if (method.includes("SceneScheduleHistory")) {
        page.querySelector("#sceneScheduleHistoryOutcome").value = "Succeeded";
    } else if (method.includes("Configuration")) {
        page._huePageGeneration += 1;
    } else {
        page.querySelector("#sessionHistoryOutcome").value = "Completed";
    }
}

function assertExportPayload(testCase, download) {
    assert.equal(download.fileName, testCase.fileName, `${testCase.method} file name`);
    assert.ok(download.blob, `${testCase.method} creates a blob`);
    assert.equal(download.blob.type, testCase.contentType, `${testCase.method} content type`);
    const payload = download.blob.parts.join("");
    if (testCase.dataType === "json") {
        assert.deepEqual(JSON.parse(payload), testCase.response, `${testCase.method} JSON payload`);
    } else {
        assert.equal(payload, testCase.response, `${testCase.method} text payload`);
    }
}

async function testSuccessfulExport(testCase) {
    const harness = makeHarness();
    const { page, api, requests, downloads } = harness;
    const operation = api[testCase.method](page);
    assert.ok(operation && typeof operation.then === "function", `${testCase.method} returns a promise`);
    assert.equal(requests.length, 1, `${testCase.method} starts one request`);
    assert.equal(requests[0].options.type, "GET", `${testCase.method} uses GET`);
    assert.equal(requests[0].options.dataType, testCase.dataType, `${testCase.method} response type`);
    assert.equal(requests[0].options.url, testCase.route, `${testCase.method} query scope`);
    assert.equal(page[testCase.flag], true, `${testCase.method} marks itself busy`);
    assert.equal(page.querySelector(testCase.button).disabled, true, `${testCase.method} disables its button`);

    requests[0].resolve(testCase.response);
    await operation;

    assert.equal(downloads.length, 1, `${testCase.method} downloads once`);
    assertExportPayload(testCase, downloads[0]);
    assert.equal(page[testCase.flag], false, `${testCase.method} clears busy state`);
    assert.equal(page.querySelector(testCase.button).disabled, false, `${testCase.method} re-enables its button`);
}

async function testStaleQuerySuppressesExport(testCase) {
    const harness = makeHarness();
    const { page, api, requests, downloads } = harness;
    const operation = api[testCase.method](page);
    assert.equal(requests.length, 1, `${testCase.method} starts a stale-query request`);
    mutateQuery(page, testCase.method);
    requests[0].resolve(testCase.response);
    await operation;

    assert.equal(downloads.length, 0, `${testCase.method} suppresses a stale-query download`);
    assert.equal(page[testCase.flag], false, `${testCase.method} clears stale-query busy state`);
    assert.equal(page.querySelector(testCase.button).disabled, false, `${testCase.method} re-enables after stale query`);
    assert.equal(page._huePageRequests[testCase.key], undefined, `${testCase.method} removes stale request record`);
    assert.equal(requests[0].promise.aborted, true, `${testCase.method} aborts stale request record`);
}

async function testInvalidatedPageSuppressesExport(testCase) {
    const harness = makeHarness();
    const { page, api, requests, downloads } = harness;
    const operation = api[testCase.method](page);
    assert.equal(requests.length, 1, `${testCase.method} starts an invalidation request`);
    api.invalidatePageLifecycle(page);
    assert.equal(page._huePageActive, false, `${testCase.method} marks page inactive`);
    assert.equal(page[testCase.flag], false, `${testCase.method} clears busy state on invalidation`);
    assert.equal(page.querySelector(testCase.button).disabled, false, `${testCase.method} re-enables button on invalidation`);
    assert.equal(page._huePageRequests[testCase.key], undefined, `${testCase.method} removes invalidated request record`);
    assert.equal(requests[0].promise.aborted, true, `${testCase.method} aborts invalidated request`);
    requests[0].resolve(testCase.response);
    await operation;
    assert.equal(downloads.length, 0, `${testCase.method} suppresses an invalidated download`);
}

async function testCurrentFailure(testCase) {
    const harness = makeHarness();
    const { page, api, requests, downloads } = harness;
    const operation = api[testCase.method](page);
    requests[0].reject(new Error("deterministic export failure"));
    await operation;
    assert.equal(downloads.length, 0, `${testCase.method} does not download failures`);
    assert.equal(page[testCase.flag], false, `${testCase.method} clears failed busy state`);
    assert.equal(page.querySelector(testCase.button).disabled, false, `${testCase.method} re-enables after failure`);
}

async function testDuplicateClickIsBounded(testCase) {
    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    const first = api[testCase.method](page);
    const second = api[testCase.method](page);
    assert.ok(first && typeof first.then === "function", `${testCase.method} first click returns a promise`);
    if (second !== undefined) {
        assert.ok(second && typeof second.then === "function", `${testCase.method} duplicate click returns only a settled no-op promise`);
    }
    assert.equal(requests.length, 1, `${testCase.method} keeps one in-flight request`);
    requests[0].resolve(testCase.response);
    await first;
}

async function testEditMappingLifecycleGuards() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    for (const method of [
        "toggleMappingSyncFields",
        "toggleMappingManualArea",
        "refreshMappingDeviceRoutes",
        "loadMappingAreas",
        "showStoredMappingArea"
    ]) {
        api[method] = () => {};
    }

    const first = api.editUserMapping(page, "user-one", "mapping-one");
    assert.equal(requests.length, 1, "first mapping edit starts one request");
    const second = api.editUserMapping(page, "user-two", "mapping-two");
    assert.equal(requests.length, 2, "second mapping edit starts one request");
    assert.equal(requests[0].promise.aborted, true, "second mapping edit aborts the superseded request");

    page.querySelector("#mappingFormLegend").textContent = "unchanged while stale";
    requests[0].resolve([{
        MappingId: "mapping-one",
        UserId: "user-one",
        UserName: "User One",
        SyncEnabled: false
    }]);
    await first;
    assert.equal(
        page.querySelector("#mappingFormLegend").textContent,
        "unchanged while stale",
        "superseded mapping edit cannot overwrite the form");

    requests[1].resolve([{
        MappingId: "mapping-two",
        UserId: "user-two",
        UserName: "User Two",
        SyncEnabled: false
    }]);
    await second;
    assert.equal(
        page.querySelector("#mappingFormLegend").textContent,
        "Edit User Mapping",
        "current mapping edit populates the form");

    page.querySelector("#mappingFormLegend").textContent = "unchanged after pagehide";
    const afterPagehide = api.editUserMapping(page, "user-three", "mapping-three");
    assert.equal(requests.length, 3, "pagehide mapping edit starts one request");
    api.invalidatePageLifecycle(page);
    assert.equal(requests[2].promise.aborted, true, "pagehide aborts the mapping edit request");
    requests[2].resolve([{
        MappingId: "mapping-three",
        UserId: "user-three",
        UserName: "User Three",
        SyncEnabled: false
    }]);
    await afterPagehide;
    assert.equal(
        page.querySelector("#mappingFormLegend").textContent,
        "unchanged after pagehide",
        "invalidated mapping edit cannot overwrite the hidden page");
}

async function testConfigurationImportValidationLifecycleGuards() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    api.applyConfigurationImportCredentials = () => true;
    page._hueImportDocument = { Configuration: { HueBridgeIp: "bridge.local" } };

    const first = api.validateConfigurationImport(page);
    assert.ok(first && typeof first.then === "function", "import validation returns a promise");
    assert.equal(requests.length, 1, "first import validation starts one request");
    assert.equal(requests[0].options.type, "POST", "import validation uses POST");
    assert.equal(requests[0].options.url, "HueSync/Configuration/ValidateImport", "import validation uses the preflight endpoint");
    assert.equal(page.querySelector("#validateConfigurationImportBtn").disabled, true, "validation disables its button while pending");

    const second = api.validateConfigurationImport(page);
    assert.equal(requests.length, 2, "second import validation starts one replacement request");
    assert.equal(requests[0].promise.aborted, true, "second import validation aborts the superseded request");
    page.querySelector("#configurationPortabilityStatus").textContent = "current validation marker";
    requests[0].resolve({
        valid: true,
        canImport: true,
        message: "stale validation"
    });
    await first;
    assert.equal(
        page.querySelector("#configurationPortabilityStatus").textContent,
        "current validation marker",
        "superseded import validation cannot overwrite current status");
    assert.equal(page.querySelector("#applyConfigurationImportBtn").disabled, true, "superseded import validation cannot enable import");

    requests[1].resolve({
        valid: true,
        canImport: true,
        configurationVersion: "test-configuration-version",
        message: "Import is valid",
        totalMappings: 1,
        totalColorPresets: 2,
        totalScenePlaylists: 3,
        totalSceneSchedules: 4
    });
    await second;
    assert.match(
        page.querySelector("#configurationPortabilityStatus").textContent,
        /^Import is valid/,
        "current import validation reports its result");
    assert.equal(page.querySelector("#applyConfigurationImportBtn").disabled, false, "current valid import enables apply");

    page._hueImportDocument = { Configuration: { HueBridgeIp: "other-bridge.local" } };
    const afterPagehide = api.validateConfigurationImport(page);
    assert.equal(requests.length, 3, "page-scoped import validation starts one request");
    page.querySelector("#configurationPortabilityStatus").textContent = "unchanged after pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(requests[2].promise.aborted, true, "pagehide aborts import validation");
    assert.equal(page._hueImportDocument, null, "pagehide clears the pending import document");
    assert.equal(page.querySelector("#applyConfigurationImportBtn").disabled, true, "pagehide disables import approval");
    requests[2].resolve({ valid: true, canImport: true, message: "hidden stale validation" });
    await afterPagehide;
    assert.equal(
        page.querySelector("#configurationPortabilityStatus").textContent,
        "unchanged after pagehide",
        "invalidated import validation cannot update the hidden page");
}

async function testConfigurationImportFileLifecycleGuards() {
    const staleHarness = makeHarness();
    const { page, api, readers } = staleHarness;
    let prepareCalls = 0;
    api.prepareConfigurationImport = () => { prepareCalls += 1; };
    api.importConfigurationFile(page, { name: "stale-config.json" });
    assert.equal(readers.length, 1, "configuration import creates one file reader");
    const staleReader = readers[0];
    page.querySelector("#configurationPortabilityStatus").textContent = "unchanged after pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(staleReader.aborted, true, "pagehide aborts a pending configuration file reader");
    assert.equal(page._hueImportReader, null, "pagehide clears the pending configuration file reader");
    staleReader.resolve(JSON.stringify({
        SchemaVersion: 1,
        Configuration: { HueBridgeIp: "stale-bridge" }
    }));
    assert.equal(page._hueImportDocument, null, "a stale file read cannot repopulate the import document");
    assert.equal(
        page.querySelector("#configurationPortabilityStatus").textContent,
        "unchanged after pagehide",
        "a stale file read cannot update hidden-page status"
    );
    assert.equal(prepareCalls, 0, "a stale file read cannot prepare an import");

    const errorHarness = makeHarness();
    const errorPage = errorHarness.page;
    const errorApi = errorHarness.api;
    let errorPrepareCalls = 0;
    errorApi.prepareConfigurationImport = () => { errorPrepareCalls += 1; };
    errorApi.importConfigurationFile(errorPage, { name: "error-config.json" });
    assert.equal(errorHarness.readers.length, 1, "error-path import creates one file reader");
    const errorReader = errorHarness.readers[0];
    errorPage.querySelector("#configurationPortabilityStatus").textContent = "error sentinel after pagehide";
    errorApi.invalidatePageLifecycle(errorPage);
    errorReader.reject(new Error("stale file read failure"));
    assert.equal(
        errorPage.querySelector("#configurationPortabilityStatus").textContent,
        "error sentinel after pagehide",
        "a stale file-read error cannot update hidden-page status"
    );
    assert.equal(errorPrepareCalls, 0, "a stale file-read error cannot prepare an import");

    const activeHarness = makeHarness();
    const activePage = activeHarness.page;
    const activeApi = activeHarness.api;
    let preparedDocument;
    activeApi.prepareConfigurationImport = (_page, importDocument) => {
        preparedDocument = importDocument;
    };
    activeApi.importConfigurationFile(activePage, { name: "active-config.json" });
    assert.equal(activeHarness.readers.length, 1, "active import creates one file reader");
    activeHarness.readers[0].resolve(JSON.stringify({
        SchemaVersion: 1,
        Configuration: { HueBridgeIp: "active-bridge" }
    }));
    assert.equal(preparedDocument.SchemaVersion, 1, "a current file read preserves the export schema version");
    assert.equal(
        preparedDocument.Configuration.HueBridgeIp,
        "active-bridge",
        "a current file read prepares the selected import"
    );
    assert.equal(activePage._hueImportReader, null, "a completed file read clears its reader state");
}

async function testMappingDeviceRouteCredentialScope() {
    const harness = makeHarness();
    const { api } = harness;
    const userOne = "12345678-1234-1234-1234-1234567890ab";
    const userTwo = "87654321-4321-4321-4321-ba0987654321";
    const deviceId = "shared-playback-device";
    const mappingOne = "mapping-one";
    const mappingTwo = "mapping-two";
    const bridgeOne = "192.168.1.10.";
    const bridgeTwo = "192.168.1.11";

    const firstKey = api.getMappingDeviceRouteCredentialKey(deviceId, bridgeOne, userOne, mappingOne);
    const secondUserKey = api.getMappingDeviceRouteCredentialKey(deviceId, bridgeOne, userTwo, mappingTwo);
    const secondBridgeKey = api.getMappingDeviceRouteCredentialKey(deviceId, bridgeTwo, userOne, mappingOne);
    assert.notEqual(firstKey, secondUserKey, "route credential keys isolate mappings for different users");
    assert.notEqual(firstKey, secondBridgeKey, "route credential keys isolate bridge changes");

    api._hueDeviceRouteCredentials = {};
    api.rememberMappingDeviceRouteCredentials(deviceId, "app-one", "client-one", bridgeOne, userOne, mappingOne);
    api.rememberMappingDeviceRouteCredentials(deviceId, "app-two", "client-two", bridgeTwo, userOne, mappingOne);
    api.rememberMappingDeviceRouteCredentials(deviceId, "app-other", "client-other", bridgeOne, userTwo, mappingTwo);

    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, "192.168.1.10", userOne, mappingOne).appKey,
        "app-one",
        "matching route identity returns its own cached App Key"
    );
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, bridgeTwo, userOne, mappingOne).appKey,
        "app-two",
        "changed bridge returns only the replacement route key"
    );
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, bridgeOne, userTwo, mappingTwo).appKey,
        "app-other",
        "different mapping owner returns only its own cached App Key"
    );
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, "192.168.1.12", userOne, mappingOne).appKey,
        "",
        "unknown bridge cannot reuse another bridge's route credentials"
    );

    api.forgetMappingDeviceRouteCredentials(deviceId, bridgeOne, userOne, mappingOne);
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, bridgeOne, userOne, mappingOne).appKey,
        "",
        "removing a route clears only its scoped cached credentials"
    );
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, bridgeTwo, userOne, mappingOne).appKey,
        "app-two",
        "removing one route scope preserves a distinct bridge scope"
    );
}

async function testStoredDeviceRouteCredentialFlags() {
    const harness = makeHarness();
    const { page, api } = harness;
    const userId = "12345678-1234-1234-1234-1234567890ab";
    const deviceId = "living-room-tv";
    api.mappingEditingUserId = userId;
    api.mappingEditingMappingId = "mapping-one";
    page.querySelector("#mappingUserSelect").value = userId;
    page.querySelector("#mappingDeviceRouteSelect").value = deviceId;
    page.querySelector("#mappingDeviceTargets").value = JSON.stringify([{
        DeviceId: deviceId,
        HasAppKey: true,
        HasClientKey: true
    }]);

    const target = { userId, deviceId };
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, false), true, "a matching route with an App Key can use stored credentials");
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, true), true, "a matching route with both keys can satisfy client-key actions");

    page.querySelector("#mappingDeviceTargets").value = JSON.stringify([{
        DeviceId: deviceId,
        HasAppKey: true,
        HasClientKey: false
    }]);
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, false), true, "a route without a Client Key remains usable for REST-only actions");
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, true), false, "a route without a Client Key fails closed for DTLS actions");

    page.querySelector("#mappingDeviceTargets").value = JSON.stringify([{
        DeviceId: deviceId,
        HasAppKey: false,
        HasClientKey: true
    }]);
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, false), false, "a route without an App Key fails closed");
}

async function testCredentialPreflightPagehideGuard() {
    const harness = makeHarness();
    const { page, api } = harness;
    let resolvePreflight;
    api.ensureBridgeCertificate = () => new Promise(resolve => { resolvePreflight = resolve; });
    let actionCalls = 0;

    const request = api.runWithBridgeCertificate(page, "192.168.1.50", null, () => {
        actionCalls += 1;
        return Promise.resolve("credential request started");
    }, "preview");
    api.invalidatePageLifecycle(page);
    resolvePreflight(true);

    await assert.rejects(request, error => error && error.huePageLifecycleStale === true, "stale preflight rejects with a lifecycle marker");
    assert.equal(actionCalls, 0, "a certificate approval completed after pagehide cannot start a credential request");
}

async function testCredentialPreflightCancelGuard() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    let resolvePreflight;
    api.ensureBridgeCertificate = () => new Promise(resolve => { resolvePreflight = resolve; });
    let actionCalls = 0;

    const request = api.runWithBridgeCertificate(page, "192.168.1.50", null, () => {
        actionCalls += 1;
        return Promise.resolve("credential request started");
    }, "preview");
    page._huePreviewRequest = request;
    api.cancelPreview(page);
    assert.equal(requests.length, 1, "canceling a preflight requests server-side diagnostic cleanup");
    requests[0].resolve({ canceled: false });
    resolvePreflight(true);

    await assert.rejects(request, error => error && error.huePageLifecycleStale === true, "canceled preflight rejects with a lifecycle marker");
    assert.equal(actionCalls, 0, "explicit diagnostic cancellation cannot start a credential request after approval");
}

async function testCredentialLifecyclePreflightPagehideGuard() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    let resolvePreflight;
    api.ensureBridgeCertificate = () => new Promise(resolve => { resolvePreflight = resolve; });

    const request = api.getCredentialLifecycleRequest(
        page,
        page._huePageGeneration,
        "credentialLifecycle",
        "192.168.1.50",
        null,
        "HueSync/EntertainmentAreas",
        { type: "POST", data: JSON.stringify({ ipAddress: "192.168.1.50", appKey: "secret" }) },
        { bridgeIp: "192.168.1.50", appKey: "secret" });
    api.invalidatePageLifecycle(page);
    resolvePreflight(true);

    await assert.rejects(request, error => error && error.huePageLifecycleStale === true, "stale lifecycle preflight rejects with a lifecycle marker");
    assert.equal(requests.length, 0, "a stale lifecycle preflight cannot start the credential-bearing API request");
}

async function testRegistrationLifecycleGuards() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    let resolvePreflight;
    api.ensureBridgeCertificate = () => new Promise(resolve => { resolvePreflight = resolve; });
    page.querySelector("#hueBridgeIp").value = "192.168.1.50";

    api.registerBridge(page);
    api.invalidatePageLifecycle(page);
    resolvePreflight(true);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(requests.length, 0, "a registration preflight completed after pagehide cannot start the link request");

    const mappingHarness = makeHarness();
    const mappingApi = mappingHarness.api;
    const mappingPage = mappingHarness.page;
    let resolveMappingPreflight;
    mappingApi.ensureBridgeCertificate = () => new Promise(resolve => { resolveMappingPreflight = resolve; });
    mappingHarness.page.querySelector("#mappingBridgeIp").value = "192.168.1.51";
    mappingApi.registerMappingBridge();
    mappingApi.invalidatePageLifecycle(mappingPage);
    resolveMappingPreflight(true);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(mappingHarness.requests.length, 0, "a mapping registration preflight completed after pagehide cannot start the link request");
}

async function testMappingDeviceRouteChannelIsolation() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    // Keep this existing request-contract test focused on route payloads; the
    // certificate preflight has its own static contract checks.
    api.ensureBridgeCertificate = () => Promise.resolve(true);
    const userId = "12345678-1234-1234-1234-1234567890ab";
    const deviceId = "living-room-tv";

    api.mappingEditingUserId = userId;
    api.mappingEditingMappingId = "mapping-one";
    page.querySelector("#mappingUserSelect").value = userId;
    page.querySelector("#mappingDeviceRouteSelect").value = deviceId;
    page.querySelector("#mappingDeviceTargets").value = JSON.stringify([
        {
            DeviceId: deviceId,
            DeviceName: "Living room TV",
            HueBridgeIp: "192.168.1.50",
            HasAppKey: true,
            HasClientKey: true,
            EntertainmentAreaId: "route-area",
            ChannelIdsOverride: "7, 8"
        }
    ]);
    page.querySelector("#mappingDeviceRouteId").value = deviceId;
    page.querySelector("#mappingDeviceRouteBridge").value = "192.168.1.50";
    page.querySelector("#mappingDeviceRouteAppKey").value = "route-app-key";
    page.querySelector("#mappingDeviceRouteClientKey").value = "route-client-key";
    page.querySelector("#mappingDeviceRouteAreaId").value = "route-area";
    page.querySelector("#mappingDeviceRouteChannels").value = "7, 8";
    page.querySelector("#mappingChannelIdsOverride").value = "1, 2";
    page.querySelector("#channelIds").value = "3, 4";

    assert.equal(
        api.getEffectiveMappingChannelIds(page),
        "7, 8",
        "a selected route uses its route-specific channel profile"
    );

    const routeLoad = api.loadMappingDeviceRouteChannels();
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(requests.length, 1, "route channel loading starts one request");
    assert.equal(requests[0].options.url, "HueSync/EntertainmentChannels", "route channel loading uses the channel endpoint");
    const routePayload = JSON.parse(requests[0].options.data);
    assert.deepEqual(
        routePayload,
        {
            ipAddress: "192.168.1.50",
            appKey: "route-app-key",
            entertainmentAreaId: "route-area",
            userId,
            deviceId
        },
        "route channel loading sends the selected route identity"
    );
    requests[0].resolve([{ channelId: 9 }, { ChannelId: 4 }]);
    await routeLoad;
    assert.equal(page.querySelector("#mappingDeviceRouteChannels").value, "4, 9", "route loading writes only the route channel field");
    assert.equal(page.querySelector("#mappingChannelIdsOverride").value, "1, 2", "route loading does not mutate the outer user profile");

    api.testMappingConnection();
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(requests.length, 2, "mapping connection test starts one request");
    const connectionPayload = JSON.parse(requests[1].options.data);
    assert.equal(connectionPayload.channelIds, "4, 9", "mapping connection test uses the selected route channels");
    assert.equal(connectionPayload.deviceId, deviceId, "mapping connection test keeps the selected route identity");
    const connectionRequest = page._huePreviewRequest;
    requests[1].resolve({ areaFound: true, channelProfileValid: true, streamTested: false });
    await connectionRequest;
    await new Promise(resolve => setTimeout(resolve, 0));

    page._huePreviewTargetMetadataReady = true;
    api.previewMappingColor(page);
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(requests.length, 3, "mapping preview starts one request");
    const previewPayload = JSON.parse(requests[2].options.data);
    assert.equal(previewPayload.channelIds, "4, 9", "mapping preview uses the selected route channels");
    assert.equal(previewPayload.deviceId, deviceId, "mapping preview keeps the selected route identity");
    const previewRequest = page._huePreviewRequest;
    requests[2].resolve({ succeeded: true, message: "preview complete" });
    await previewRequest;
    await new Promise(resolve => setTimeout(resolve, 0));

    page.querySelector("#mappingDeviceRouteChannels").value = "";
    assert.equal(
        api.getEffectiveMappingChannelIds(page),
        "1, 2",
        "a blank staged route profile inherits the outer user profile"
    );

    page.querySelector("#mappingBridgeIp").value = "192.168.1.60";
    page.querySelector("#mappingAppKey").value = "outer-app-key";
    page.querySelector("#mappingAreaSelect").value = "outer-area";
    page.querySelector("#mappingDeviceRouteChannels").value = "4, 9";
    const outerLoad = api.loadMappingChannels(page);
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(requests.length, 4, "outer mapping channel loading starts a separate request");
    const outerPayload = JSON.parse(requests[3].options.data);
    assert.equal(outerPayload.ipAddress, "192.168.1.60", "outer channel loading ignores the selected route bridge");
    assert.equal(outerPayload.entertainmentAreaId, "outer-area", "outer channel loading ignores the selected route area");
    assert.equal(outerPayload.deviceId, "", "outer channel loading does not send a device route");
    requests[3].resolve([{ channelId: 2 }]);
    await outerLoad;
    assert.equal(page.querySelector("#mappingChannelIdsOverride").value, "2", "outer loading writes the outer user profile");
    assert.equal(page.querySelector("#mappingDeviceRouteChannels").value, "4, 9", "outer loading preserves the route profile");
}

async function testConfigurationImportSubmitLifecycleGuards() {
    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    api.applyConfigurationImportCredentials = () => true;
    api.loadConfiguration = () => {};
    api.loadUserMappings = () => {};
    api.loadColorPresets = () => {};
    api.loadScenePlaylists = () => {};
    api.loadSceneSchedules = () => {};
    dashboard.confirm = (title, message, callback) => callback(true);
    page._hueImportDocument = { Configuration: { HueBridgeIp: "bridge.local" } };
    page._hueImportValidated = true;
    page._hueImportConfigurationVersion = "test-configuration-version";

    api.submitConfigurationImport(page);
    assert.equal(requests.length, 1, "configuration import starts one request after confirmation");
    assert.equal(requests[0].options.type, "POST", "configuration import uses POST");
    assert.equal(requests[0].options.url, "HueSync/Configuration/Import", "configuration import uses the import endpoint");
    assert.equal(JSON.parse(requests[0].options.data).expectedConfigurationVersion, "test-configuration-version", "configuration import sends the validated snapshot token");

    page.querySelector("#configurationPortabilityStatus").textContent = "unchanged after pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(requests[0].promise.aborted, true, "pagehide aborts configuration import UI request");
    requests[0].resolve({ message: "hidden stale import" });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(
        page.querySelector("#configurationPortabilityStatus").textContent,
        "unchanged after pagehide",
        "invalidated configuration import cannot update the hidden page");
    assert.equal(page._hueImportDocument, null, "pagehide clears the imported document before a later confirmation");
}

async function testConfigurationSaveSuppressesStaleConfigurationLoad() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    api.loadEntertainmentAreas = () => {};
    api.loadColorPresets = () => {};
    api.loadScenePlaylists = () => {};
    api.loadSceneSchedules = () => {};
    api.refreshStatus = (currentPage, message) => {
        currentPage.querySelector('#configurationPortabilityStatus').textContent = message;
    };

    const load = api.loadConfiguration(page);
    assert.equal(requests.length, 1, "configuration save race starts with one tracked load");
    page.querySelector('#hueBridgeIp').value = "edited-bridge";
    const save = api.saveConfiguration(page);
    assert.equal(requests.length, 2, "configuration save starts one replacement request");
    assert.equal(requests[0].promise.aborted, true, "configuration save aborts the stale configuration load");
    assert.equal(requests[1].options.type, "POST", "configuration save uses POST");
    assert.equal(requests[1].options.url, "HueSync/Configuration", "configuration save uses the configuration endpoint");
    assert.equal(page.querySelector('#saveConfigurationBtn').disabled, true, "configuration save disables its button");

    requests[0].resolve({ HueBridgeIp: "old-bridge", SyncEnabled: true });
    await load;
    assert.equal(
        page.querySelector('#hueBridgeIp').value,
        "edited-bridge",
        "stale configuration load cannot overwrite edits made before save"
    );

    requests[1].resolve({ HueBridgeIp: "edited-bridge", HasAppKey: false, HasClientKey: false });
    await save;
    assert.equal(
        page.querySelector('#configurationPortabilityStatus').textContent,
        "Configuration saved.",
        "current configuration save reports success"
    );
    assert.equal(api.globalBridgeIp, "edited-bridge", "current configuration save updates the global target");
    assert.equal(page.querySelector('#saveConfigurationBtn').disabled, false, "configuration save re-enables its button");
}

async function testConfigurationSaveInvalidationSuppressesCallbacks() {
    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    api.refreshStatus = (currentPage, message) => {
        currentPage.querySelector('#configurationPortabilityStatus').textContent = message;
    };
    const save = api.saveConfiguration(page);
    assert.equal(requests.length, 1, "configuration save starts one tracked request");
    page.querySelector('#configurationPortabilityStatus').textContent = "sentinel before pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(requests[0].promise.aborted, true, "pagehide aborts configuration save");
    assert.equal(page._hueConfigurationSaving, false, "pagehide clears configuration save state");
    assert.equal(page.querySelector('#saveConfigurationBtn').disabled, false, "pagehide re-enables the save button");
    assert.equal(page._huePageRequests.configurationSave, undefined, "pagehide removes the save request record");
    requests[0].resolve({ HueBridgeIp: "stale-bridge" });
    await save;
    assert.equal(
        page.querySelector('#configurationPortabilityStatus').textContent,
        "sentinel before pagehide",
        "invalidated configuration save cannot update hidden-page status"
    );
    assert.equal(dashboard.alerts.length, 0, "invalidated configuration save cannot show a stale alert");
}

async function testConfigurationSaveDuplicateSubmitIsBounded() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    const statusMessages = [];
    api.refreshStatus = (_currentPage, message) => statusMessages.push(message);
    const first = api.saveConfiguration(page);
    const second = api.saveConfiguration(page);
    assert.equal(requests.length, 1, "duplicate configuration submit keeps one in-flight request");
    assert.ok(second && typeof second.then === "function", "duplicate configuration submit returns a settled no-op");
    assert.equal(page._hueConfigurationSaving, true, "configuration save remains marked busy while pending");
    requests[0].resolve({ HueBridgeIp: "saved-bridge", HasAppKey: false, HasClientKey: false });
    await Promise.all([first, second]);
    assert.deepEqual(statusMessages, ["Configuration saved."], "duplicate configuration submit reports one result");
    assert.equal(page._hueConfigurationSaving, false, "configuration save clears its busy state");
    assert.equal(page.querySelector('#saveConfigurationBtn').disabled, false, "duplicate configuration submit re-enables the button");
}

async function testDuplicateTargetNormalizationAndGuard() {
    const harness = makeHarness();
    const { page, api, dashboard } = harness;
    const duplicateUserId = "12345678-1234-1234-1234-1234567890ab";
    const duplicateState = api.getTargetMappingDuplicateState([
        { UserId: "{" + duplicateUserId.toUpperCase() + "}", UserName: "Upper row", SyncEnabled: true },
        { userId: "123456781234123412341234567890ab", userName: "Compact row", syncEnabled: false },
        { userId: "disabled-user", userName: "Disabled one", syncEnabled: false },
        { userId: "DISABLED-USER", userName: "Disabled two", syncEnabled: false }
    ]);
    const normalizedKey = duplicateUserId.toLowerCase();
    assert.equal(duplicateState.duplicateUserIds[normalizedKey], true, "duplicate state normalizes wrapped and compact Jellyfin IDs");
    assert.equal(duplicateState.enabledDuplicateUserIds[normalizedKey], true, "one enabled row keeps a normalized duplicate group blocked");
    assert.equal(duplicateState.hasEnabledDuplicates, true, "enabled duplicate groups are detected");
    assert.equal(duplicateState.enabledDuplicateUserIds["disabled-user"], undefined, "all-disabled duplicate groups do not block broadcasts");

    const duplicateRouteValue = api.encodeCurrentLightDeviceTarget(duplicateUserId, "device-1");
    const targetOptions = [
        { value: "__all_enabled_targets__", textContent: "All enabled targets", disabled: false, title: "" },
        { value: duplicateUserId, textContent: "Upper row", disabled: false, title: "" },
        { value: duplicateRouteValue, textContent: "↳ Living room", disabled: false, title: "" }
    ];
    api.annotateTargetOptions({ options: targetOptions }, duplicateState);
    assert.equal(targetOptions[0].disabled, true, "enabled duplicates disable the all-target option");
    assert.equal(targetOptions[1].disabled, true, "enabled duplicates disable the affected mapping option");
    assert.equal(targetOptions[2].disabled, true, "enabled duplicates disable nested device routes");
    assert.match(targetOptions[1].textContent, /duplicate mapping/, "affected mapping options are annotated");

    const disabledOnlyState = api.getTargetMappingDuplicateState([
        { userId: "disabled-user", userName: "Disabled one", syncEnabled: false },
        { userId: "DISABLED-USER", userName: "Disabled two", syncEnabled: false }
    ]);
    const disabledOnlyOptions = [
        { value: "__all_enabled_targets__", textContent: "All enabled targets", disabled: false, title: "" },
        { value: "disabled-user", textContent: "Disabled one", disabled: false, title: "" }
    ];
    api.annotateTargetOptions({ options: disabledOnlyOptions }, disabledOnlyState);
    assert.equal(disabledOnlyOptions[0].disabled, false, "all-disabled duplicate groups do not disable broadcasts");
    assert.equal(disabledOnlyOptions[1].disabled, true, "all-disabled duplicate mapping rows remain unselectable");

    page._hueTargetMappingDuplicateState = duplicateState;
    const status = page.querySelector("#previewColorStatus");
    assert.equal(api.blockDuplicateTargetBroadcast(page, status), true, "enabled duplicates block broadcast actions");
    assert.match(status.textContent, /Resolve Duplicate Mappings/, "broadcast guard gives an actionable resolution path");
    assert.equal(dashboard.alerts.length, 1, "broadcast guard announces the duplicate mapping block");
}

async function testDuplicateMappingResolutionLifecycleGuards() {
    const duplicateUserId = "12345678-1234-1234-1234-1234567890ab";
    const report = {
        ReportVersion: "report-version-1",
        Mappings: [
            {
                Status: "DuplicateMapping",
                MappingId: "mapping-keeper",
                CanonicalUserId: duplicateUserId,
                PersistedUserName: "Keeper"
            },
            {
                Status: "DuplicateMapping",
                MappingId: "mapping-sibling",
                CanonicalUserId: duplicateUserId,
                PersistedUserName: "Sibling"
            }
        ]
    };

    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    const followUps = [];
    let confirmation;
    dashboard.confirm = (_message, _title, callback) => { confirmation = callback; };
    api.loadUserMappings = () => { followUps.push("mappings"); };
    api.reconcileUserMappings = () => { followUps.push("reconcile"); };
    api.refreshTargetMetadata = () => {
        followUps.push("metadata");
        return Promise.resolve();
    };

    api.renderDuplicateResolutionControls(page, report);
    const container = page.querySelector("#userMappingDuplicateResolutionControls");
    const resolveButton = container.children.find(child => child.tagName === "BUTTON");
    assert.ok(resolveButton, "duplicate resolution renders an action button");
    resolveButton.listeners.click();
    assert.equal(typeof confirmation, "function", "duplicate resolution asks for confirmation");
    confirmation(true);
    assert.equal(requests.length, 1, "confirmed duplicate resolution starts one request");
    assert.equal(requests[0].options.type, "POST", "duplicate resolution uses POST");
    assert.equal(requests[0].options.url, "HueSync/UserMappings/ResolveDuplicates", "duplicate resolution uses the API route");
    assert.deepEqual(JSON.parse(requests[0].options.data), {
        retainMappingId: "mapping-keeper",
        removeMappingIds: ["mapping-sibling"],
        expectedReportVersion: "report-version-1"
    }, "duplicate resolution sends the exact selected rows and report version");

    page.querySelector("#userMappingReconcileStatus").textContent = "unchanged after pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(requests[0].promise.aborted, true, "pagehide aborts duplicate resolution");
    assert.equal(page._huePageRequests.userMappingDuplicateResolution, undefined, "pagehide removes duplicate resolution request state");
    requests[0].resolve({ RemovedCount: 1 });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(
        page.querySelector("#userMappingReconcileStatus").textContent,
        "unchanged after pagehide",
        "invalidated duplicate resolution cannot update hidden-page status"
    );
    assert.deepEqual(followUps, [], "invalidated duplicate resolution cannot trigger stale follow-up loads");

    const confirmationHarness = makeHarness();
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => { confirmation = callback; };
    confirmationApi.renderDuplicateResolutionControls(confirmationPage, report);
    const confirmationContainer = confirmationPage.querySelector("#userMappingDuplicateResolutionControls");
    const confirmationButton = confirmationContainer.children.find(child => child.tagName === "BUTTON");
    confirmationButton.listeners.click();
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "a confirmation completed after pagehide cannot start duplicate resolution");
}

for (const testCase of exportCases) {
    await testSuccessfulExport(testCase);
    await testStaleQuerySuppressesExport(testCase);
    await testInvalidatedPageSuppressesExport(testCase);
    await testCurrentFailure(testCase);
    await testDuplicateClickIsBounded(testCase);
}

await testEditMappingLifecycleGuards();
await testConfigurationImportValidationLifecycleGuards();
await testConfigurationImportFileLifecycleGuards();
await testMappingDeviceRouteCredentialScope();
await testStoredDeviceRouteCredentialFlags();
await testCredentialPreflightPagehideGuard();
await testCredentialPreflightCancelGuard();
await testCredentialLifecyclePreflightPagehideGuard();
await testRegistrationLifecycleGuards();
await testMappingDeviceRouteChannelIsolation();
await testConfigurationImportSubmitLifecycleGuards();
await testConfigurationSaveSuppressesStaleConfigurationLoad();
await testConfigurationSaveInvalidationSuppressesCallbacks();
await testConfigurationSaveDuplicateSubmitIsBounded();
await testDuplicateTargetNormalizationAndGuard();
await testDuplicateMappingResolutionLifecycleGuards();

console.log(`Configuration lifecycle contracts passed (${exportCases.length} exports plus mapping-edit, scoped route credentials/channel isolation, certificate preflight/cancel/pagehide, registration lifecycle, import file/validation/submit, save stale-scope/pagehide, duplicate-target, and duplicate-resolution paths)`);
