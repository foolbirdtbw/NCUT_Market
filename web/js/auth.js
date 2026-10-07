/* 登录状态：token 的存取、当前用户、登录/注册页、顶栏那块用户区。
 *
 * token 存 localStorage。这不如同源 httpOnly Cookie + CSRF 稳，是选 JWT 时就认下的代价；
 * 缓解手段是全站 esc()、上传的图一律重新编码、静态响应带 nosniff。 */
window.auth = (function ($) {
  "use strict";

  var NM = window.NM;

  var TOKEN_KEY = "ncut.token";
  var USER_KEY = "ncut.user";

  /* 登录状态一变就要跟着动的地方（未读徽标是第一个）。
   * 三条路都会经过 paint()——signIn、signOut、refresh——所以通知挂在它开头，
   * 不在那三处各写一遍：漏一处就是一处不同步，而且很难发现。 */
  var listeners = [];

  function onUserChanged(callback) {
    listeners.push(callback);
  }

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
    /* 先通知，再管顶栏那块。顶栏元素不在页面上时就提前返回了，
     * 通知写在后面的话会跟着一起被跳过。 */
    listeners.forEach(function (callback) { callback(); });

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

    /* 顶栏只剩一个入口，退出按钮挪到个人主页里了（products.js 的 showMine）——
     * 它和导航链接长得一样、挨得又近，点错的代价是当场登出。
     *
     * 兜底文本跟着页面叫「个人主页」。它几乎不会出现（注册时昵称是必填的），但顶栏左边那条
     * 也叫这个名字，兜底要还是旧名就成了两个标签指同一个地方。 */
    slot.html('<a class="nav-user" href="#/mine">' + NM.esc(current.nickname || "个人主页") + '</a>');
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
      '<p class="muted form-foot">还没有账号？<a href="#/register">注册一个</a>' +
      ' · <a href="#/forgot">忘记密码？</a></p>' +
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
      /* type="text" 而不是 number：学号末四位是编号，有 0157 这种前导零，number 会把它吃掉。 */
      '<div class="field-stack">' +
      '<label for="register-student-id">学号</label>' +
      '<input type="text" id="register-student-id" inputmode="numeric" required>' +
      '<span class="hint">忘了密码要靠它认人，注册后不能改。</span>' +
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
    var studentId = $("#register-student-id").val().trim();
    var password = $("#register-password").val();

    /* 客户端先挡一道，只是为了少一次往返。真正的规则在服务端，那边才是权威——
     * 这里放过去的任何东西都必须能被服务端再拒一次。
     *
     * 学号这里只查了空，没有照抄服务端那条正则：用户名长度同样是服务端在管，
     * 两边各写一份迟早会跑偏，而跑偏的那一份不报错，只是悄悄放行。 */
    if (!username || !nickname || !studentId || !password) {
      $("#register-error").html(NM.inlineError({ message: "四个字段都要填。" }));
      return;
    }

    $("#register-error").empty();

    api.post("/api/auth/register", {
      username: username,
      nickname: nickname,
      studentId: studentId,
      password: password
    }).then(function (response) {
      // 注册接口直接返回 token，所以不用再发一次登录请求。
      signIn(response);
      location.hash = "#/products";
    }, function (error) {
      $("#register-error").html(NM.inlineError(error));
    });
  }

  /* ---------- 忘记密码 ---------- */

  /* 找回密码。没有邮箱也没有手机号，所以流程是：在企业微信上找管理员，报学号核验身份，
   * 管理员在用户管理页按学号搜到人、生成一串一次性重置码，用户在这一页拿它换新密码。
   *
   * 新密码是用户自己设的，管理员看不到——这正是选重置码而不是「管理员给个临时密码」
   * 的理由。 */
  function showForgot() {
    $("#view").html(
      '<div class="card form-card">' +
      '<h1>重置密码</h1>' +
      '<p class="muted">重置密码要先拿一个一次性的码。在企业微信上找 <strong>计专研24田搏文</strong>，' +
      '报上学号核对身份，他会给你一串。</p>' +
      '<form id="forgot-form" novalidate>' +
      '<div class="field-stack">' +
      '<label for="forgot-username">用户名</label>' +
      '<input type="text" id="forgot-username" autocomplete="username" required>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="forgot-code">重置码</label>' +
      '<input type="text" id="forgot-code" autocomplete="one-time-code" required>' +
      '<span class="hint">形如 XXXX-XXXX，大小写和中间的横线都不计较。只能用一次。</span>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="forgot-password">新密码</label>' +
      '<input type="password" id="forgot-password" autocomplete="new-password" required>' +
      '<span class="hint">至少 6 个字符。设好之后这个码就作废了。</span>' +
      '</div>' +
      '<div id="forgot-error"></div>' +
      '<button class="button button-primary" type="submit">设成新密码</button>' +
      '</form>' +
      '<p class="muted form-foot">想起来了？<a href="#/login">回去登录</a></p>' +
      '</div>');
  }

  function submitForgot() {
    var username = $("#forgot-username").val().trim();
    var code = $("#forgot-code").val().trim();
    var password = $("#forgot-password").val();

    if (!username || !code || !password) {
      $("#forgot-error").html(NM.inlineError({ message: "三个字段都要填。" }));
      return;
    }

    $("#forgot-error").empty();

    /* 码原样发过去，破折号、空格、大小写都由服务端 Normalize 收拾——
     * 在这里再理一遍的话，两边规则迟早在某处跑偏。 */
    api.post("/api/auth/reset-password", {
      username: username,
      resetCode: code,
      newPassword: password
    }).then(function (response) {
      // 和注册一样，这个接口直接返回 token，所以重置完就是登录状态。
      signIn(response);
      location.hash = pendingHash || "#/products";
      pendingHash = null;
    }, function (error) {
      $("#forgot-error").html(NM.inlineError(error));
    });
  }

  return {
    token: token,
    user: user,
    isSignedIn: isSignedIn,
    onUserChanged: onUserChanged,
    signIn: signIn,
    signOut: signOut,
    expire: expire,
    refresh: refresh,
    paint: paint,
    showLogin: showLogin,
    submitLogin: submitLogin,
    showRegister: showRegister,
    submitRegister: submitRegister,
    showForgot: showForgot,
    submitForgot: submitForgot
  };
})(jQuery);
