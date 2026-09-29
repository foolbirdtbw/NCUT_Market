/* The one place the site talks to the API.
 *
 * This is not tidiness for its own sake. Every failure the API produces is an RFC 7807
 * `application/problem+json` body carrying a `code` and a `traceId`, and `traceId` is the only
 * handle that connects what a user sees to a line in the server log. If each call site parsed that
 * body itself, the first one to forget `traceId` would quietly bring back the "user quotes an id
 * that appears nowhere in the log" problem — which the API phase fixed deliberately. Centralising
 * it means the extraction order is written once and every caller gets it.
 *
 * Classic script, not a module: it defines one global and captures jQuery at parse time.
 */
window.NcutApi = (function ($) {
  "use strict";

  /**
   * Extracts the problem body from a failed XHR.
   *
   * `xhr.responseJSON` is the main path: jQuery picks a converter by Content-Type, and 3.7.1's
   * `contents.json` is /\bjson\b/ — verified to match `application/problem+json` and, just as
   * importantly, to NOT match `application/problem+xml`. That verification is why this is a safe
   * default rather than a hopeful one.
   *
   * The fallback is not paranoia. If a future jQuery narrows that regex, or a proxy rewrites the
   * Content-Type, `responseJSON` becomes undefined and every error message silently degrades to
   * "请求失败（404）" with the code and traceId dropped — a failure that looks like the API stopped
   * sending them. Parsing the text ourselves costs three lines and removes that single point of
   * failure.
   */
  function problemBody(xhr) {
    if (xhr.responseJSON) {
      return xhr.responseJSON;
    }

    try {
      return JSON.parse(xhr.responseText);
    } catch (e) {
      return null;
    }
  }

  /**
   * Turns a failed XHR into an Error carrying a human-readable message plus the `code` and
   * `traceId` the error card needs.
   */
  function describe(xhr) {
    var problem = problemBody(xhr);
    var message = null;

    // Field-level validation errors first: "标题不能为空" is far more useful to a person than the
    // generic "One or more validation errors occurred." that `title` carries.
    if (problem && problem.errors) {
      message = Object.keys(problem.errors)
        .map(function (name) {
          var messages = problem.errors[name];
          return Array.isArray(messages) ? messages.join("；") : String(messages);
        })
        .join("；");
    }

    if (!message && problem && problem.detail) {
      message = problem.detail;
    }

    if (!message && problem && problem.title) {
      message = problem.title;
    }

    // status 0 is not an HTTP status at all — jQuery reports a transport failure (offline, DNS,
    // connection refused) that way, and "请求失败（0）" would be meaningless to a reader.
    if (!message) {
      message = xhr.status === 0
        ? "无法连接到服务器，请确认网络与 API 是否正在运行。"
        : "请求失败（HTTP " + xhr.status + "）";
    }

    var error = new Error(message);
    error.status = xhr.status;
    error.code = (problem && problem.code) || null;
    error.traceId = (problem && problem.traceId) || null;

    return error;
  }

  /**
   * Issues a request and resolves with the parsed body.
   *
   * @param {string} path   Absolute API path, e.g. "/api/categories".
   * @param {object} [options]
   * @param {string} [options.method="GET"]
   * @param {object} [options.query] Query-string values. jQuery serialises and percent-encodes them;
   *   building "?page=" + n by hand is where escaping bugs and injection start.
   * @param {object} [options.body]  Serialised as JSON.
   * @returns {Promise<object>} Rejects with the Error from describe().
   */
  function request(path, options) {
    options = options || {};

    var settings = {
      url: path,
      method: options.method || "GET",
      dataType: "json"
    };

    // Built conditionally rather than spread. An earlier draft set `data` twice in one object
    // literal — the second key wins silently, and the query string would have been dropped on
    // every request that also carried a body.
    if (options.body !== undefined) {
      settings.contentType = "application/json; charset=utf-8";
      settings.data = JSON.stringify(options.body);
    } else if (options.query) {
      settings.data = options.query;
    }

    return $.ajax(settings).then(null, function (xhr) {
      throw describe(xhr);
    });
  }

  return {
    request: request,
    describe: describe
  };
})(jQuery);
