/* 登录状态：token 的存取、当前用户、登录/注册页、顶栏那块用户区。
 *
 * token 存 localStorage。这不如同源 httpOnly Cookie + CSRF 稳，是选 JWT 时就认下的代价；
 * 缓解手段是全站 esc()、上传的图一律重新编码、静态响应带 nosniff。 */
window.auth = (function ($) {
  "use strict";

  var NM = window.NM;

  var TOKEN_KEY = "ncut.token";
  var USER_KEY = "ncut.user";

  /* token 过期时想去、但被拦下来的那个地址，登录成功后送回去。
   * 必须在这里声明：整个文件是 "use strict"，漏了 var 的话赋值和读取都会抛
   * ReferenceError——而这两处一个在登录成功的回调里、一个在 401 的处理里，
   * 抛出去的表现是"按钮点了没反应"，最难查的那种。 */
  var pendingHash = null;

  /* localStorage 在隐私模式或被禁 cookie 时会直接抛异常，不是返回 null。
   * 整站不能因为读不到一个 token 就白屏，所以每次访问都包起来。 */
  function readToken() {
    try { return localStorage.getItem(TOKEN_KEY); } catch (e) { return null; }
  }

  function writeToken(value) {
    try {
      if (value) { localStorage.setItem(TOKEN_KEY, value); }
      else { localStorage.removeItem(TOKEN_KEY); }
    } catch (e) { /* 存不下就退化成"这一刷新就登出"，不影响本次会话。 */ }
  }

  function readUser() {
    try {
      var raw = localStorage.getItem(USER_KEY);
      return raw ? JSON.parse(raw) : null;
    } catch (e) {
      return null;
    }
  }

  function writeUser(user) {
    try {
      if (user) { localStorage.setItem(USER_KEY, JSON.stringify(user)); }
      else { localStorage.removeItem(USER_KEY); }
    } catch (e) { /* 同上。 */ }
  }

  function token() {
    return readToken();
  }

  function user() {
    return readUser();
  }

  function isSignedIn() {
    return !!readToken();
  }

  function signIn(response) {
    writeToken(response.token);
    writeUser(response.user);
    paint();
  }

  function signOut() {
    writeToken(null);
    writeUser(null);
    paint();
  }

  /* token 过期或被拒。清掉之后跳登录页——但如果人已经在登录页上，就别再跳了，
   * 否则会把他正在填的表单冲掉。 */
  function expire() {
    if (!isSignedIn()) {
      return;
    }

    // 记下原本要去的地方，登录成功后送回去。只在真正有地址可回时才记——
    // 从登录页本身过期的话，回去的应该是默认页而不是登录页。
    if (location.hash.indexOf("#/login") !== 0 && location.hash.indexOf("#/register") !== 0) {
      pendingHash = location.hash;
    }

    signOut();
    location.hash = "#/login";
  }

  /* 拿服务端的 /me 覆盖本地缓存。本地那份只是为了让首屏不用等一个请求，
   * 它可能是被改过的，所以凡是"这个商品是不是我的"这类判断，进页面时都要刷一次。 */
  function refresh() {
    if (!isSignedIn()) {
      paint();
      return $.Deferred().resolve(null).promise();
    }

    return api.get("/api/auth/me").then(function (current) {
      writeUser(current);
      paint();
      return current;
    }, function () {
      // 401 已经在 api.js 里走 expire() 了；其它错误也只是让人这次按未登录渲染。
      signOut();
      return null;
    });
  }

  /* ---------- 顶栏用户区 ---------- */

  function paint() {
    var slot = $("#auth-slot");

    if (!slot.length) {
      return;
    }

    if (!isSignedIn()) {
      slot.html(
        '<a class="button" href="#/login">登录</a>' +
        '<a class="button button-primary" href="#/register">注册</a>');
      return;
    }

    var current = user() || {};

    slot.html(
      '<a class="nav-user" href="#/mine">' + NM.esc(current.nickname || "我的商品") + '</a>' +
      '<button class="button" data-action="sign-out">退出</button>');
  }

  /* ---------- 登录 ---------- */

  function showLogin() {
    $("#view").html(
      '<div class="card form-card">' +
      '<h1>登录</h1>' +
      '<form id="login-form" novalidate>' +
      '<div class="field-stack">' +
      '<label for="login-username">用户名</label>' +
      '<input type="text" id="login-username" autocomplete="username" required>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="login-password">密码</label>' +
      '<input type="password" id="login-password" autocomplete="current-password" required>' +
      '</div>' +
      '<div id="login-error"></div>' +
      '<button class="button button-primary" type="submit">登录</button>' +
      '</form>' +
      '<p class="muted form-foot">还没有账号？<a href="#/register">注册一个</a></p>' +
      '</div>');
  }

  function submitLogin() {
    var username = $("#login-username").val().trim();
    var password = $("#login-password").val();

    if (!username || !password) {
      $("#login-error").html(NM.inlineError({ message: "用户名和密码都要填。" }));
      return;
    }

    $("#login-error").empty();

    api.post("/api/auth/login", { username: username, password: password }).then(function (response) {
      signIn(response);

      // 登录前想去的页面。api.js 跳登录页时把原地址记在了 hash 里。
      location.hash = pendingHash || "#/products";
      pendingHash = null;
    }, function (error) {
      $("#login-error").html(NM.inlineError(error));
    });
  }

  /* ---------- 注册 ---------- */

  function showRegister() {
    $("#view").html(
      '<div class="card form-card">' +
      '<h1>注册</h1>' +
      '<form id="register-form" novalidate>' +
      '<div class="field-stack">' +
      '<label for="register-username">用户名</label>' +
      '<input type="text" id="register-username" autocomplete="username" required>' +
      '<span class="hint">3–50 个字符，用来登录，注册后不能改。</span>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="register-nickname">昵称</label>' +
      '<input type="text" id="register-nickname" autocomplete="nickname" required>' +
      '<span class="hint">别人看到的名字。</span>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="register-password">密码</label>' +
      '<input type="password" id="register-password" autocomplete="new-password" required>' +
      '<span class="hint">至少 6 个字符。</span>' +
      '</div>' +
      '<div id="register-error"></div>' +
      '<button class="button button-primary" type="submit">注册</button>' +
      '</form>' +
      '<p class="muted form-foot">已经有账号？<a href="#/login">去登录</a></p>' +
      '</div>');
  }

  function submitRegister() {
    var username = $("#register-username").val().trim();
    var nickname = $("#register-nickname").val().trim();
    var password = $("#register-password").val();

    /* 客户端先挡一道，只是为了少一次往返。真正的规则在服务端，那边才是权威——
     * 这里放过去的任何东西都必须能被服务端再拒一次。 */
    if (!username || !nickname || !password) {
      $("#register-error").html(NM.inlineError({ message: "三个字段都要填。" }));
      return;
    }

    $("#register-error").empty();

    api.post("/api/auth/register", {
      username: username,
      nickname: nickname,
      password: password
    }).then(function (response) {
      // 注册接口直接返回 token，所以不用再发一次登录请求。
      signIn(response);
      location.hash = "#/products";
    }, function (error) {
      $("#register-error").html(NM.inlineError(error));
    });
  }

  return {
    token: token,
    user: user,
    isSignedIn: isSignedIn,
    signIn: signIn,
    signOut: signOut,
    expire: expire,
    refresh: refresh,
    paint: paint,
    showLogin: showLogin,
    submitLogin: submitLogin,
    showRegister: showRegister,
    submitRegister: submitRegister
  };
})(jQuery);
