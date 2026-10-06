/* 商品：列表与搜索、详情、发布、编辑、我的商品。
 *
 * 视图整块用 .html() 重建，事件一律委托在 document 上绑一次（在 app.js 里）。
 * 每个页面渲染完把自己需要的字典数据补进去，不预加载。 */
window.products = (function ($) {
  "use strict";

  var NM = window.NM;

  var PAGE_SIZE = 12;

  /* 分类和宿舍区两个字典在一次会话里几乎不变，拉一次存着。
   * 传图、改分类都不用再等一次往返。 */
  var dictionaries = null;

  function loadDictionaries() {
    if (dictionaries) {
      return $.Deferred().resolve(dictionaries).promise();
    }

    return $.when(
      api.get("/api/categories", { page: 1, pageSize: 100 }),
      api.get("/api/dormitory-areas", { page: 1, pageSize: 100 })
    ).then(function (categoryPage, areaPage) {
      dictionaries = {
        categories: categoryPage[0].items,
        areas: areaPage[0].items
      };

      return dictionaries;
    });
  }

  /* 价格从 JSON 来是数字，但输入框给的是字符串；两边都归一成字符串再比。 */
  function price(value) {
    return NM.formatPrice(value);
  }

  function cardHtml(item) {
    var thumb = item.thumbnailUrl
      ? '<img src="' + NM.esc(item.thumbnailUrl) + '" alt="" loading="lazy">'
      : '<span class="thumb-placeholder">暂无图片</span>';

    /* 状态徽章只在不是"在售"时出现。公开列表全是 Published，加了这个判断就等于没加；
     * 但"我的商品"那一页草稿、已下架、已售出混在一起，不标出来分不清。 */
    var badge = item.status && item.status !== 2 ? NM.statusBadge(item.status) + " " : "";

    return '<a class="product-card" href="#/products/' + item.id + '">' +
      '<div class="product-thumb">' + thumb + '</div>' +
      '<div class="product-body">' +
      '<div class="product-title">' + badge + NM.esc(item.title) + '</div>' +
      '<div class="product-price">' + price(item.price) + '</div>' +
      '<div class="product-meta">' + NM.esc(item.categoryName) + ' · ' +
      NM.esc(item.dormitoryAreaName) + ' · ' + NM.esc(NM.conditionText(item.condition)) + '</div>' +
      '<div class="product-meta">' + NM.esc(item.sellerNickname) + ' · ' +
      NM.formatDateTime(item.createdAt) + '</div>' +
      '</div></a>';
  }

  function gridHtml(page) {
    if (!page.items.length) {
      return NM.empty("没有找到商品。换个关键词试试。");
    }

    return '<div class="product-grid">' + page.items.map(cardHtml).join("") + '</div>';
  }

  /* ---------- 列表与搜索 ---------- */

  /* 筛选条件放在 hash 里，这样刷新、后退、把链接发给别人，看到的都是同一页。
   * 页面里的输入框只是它的一个视图。 */
  function listHref(query, page) {
    var params = [];

    Object.keys(query).forEach(function (key) {
      if (key !== "page" && query[key]) {
        params.push(encodeURIComponent(key) + "=" + encodeURIComponent(query[key]));
      }
    });

    if (page && page > 1) {
      params.push("page=" + page);
    }

    return "#/products" + (params.length ? "?" + params.join("&") : "");
  }

  function showList(query) {
    $("#view").html(
      '<div class="card">' +
      '<div class="card-head"><h2>商品</h2>' +
      '<a class="button button-primary" href="#/products/new">发布商品</a></div>' +
      '<form id="search-form" class="filter-bar">' +
      '<input type="search" id="filter-q" placeholder="搜索标题或描述" value="' + NM.esc(query.q || "") + '">' +
      '<select id="filter-category"><option value="">全部分类</option></select>' +
      '<select id="filter-area"><option value="">全部宿舍楼</option></select>' +
      '<select id="filter-condition"><option value="">全部成色</option>' +
      NM.conditionOptions(query.condition ? Number(query.condition) : 0) + '</select>' +
      '<select id="filter-sort">' +
      '<option value="Newest">最新发布</option>' +
      '<option value="PriceAsc">价格从低到高</option>' +
      '<option value="PriceDesc">价格从高到低</option>' +
      '</select>' +
      '<input type="number" id="filter-min" placeholder="最低价" min="0" value="' + NM.esc(query.minPrice || "") + '">' +
      '<input type="number" id="filter-max" placeholder="最高价" min="0" value="' + NM.esc(query.maxPrice || "") + '">' +
      '<button class="button" type="submit">搜索</button>' +
      '</form>' +
      '<div id="product-list">' + NM.loading() + '</div>' +
      '</div>');

    $("#filter-sort").val(query.sort || "Newest");

    // 成色下拉的"全部"是空值，它和 conditionOptions 拼在一起，所以选中项要单独设。
    $("#filter-condition").val(query.condition || "");

    /* 两个下拉都是「先补选项、再设选中」。选中必须在 append 之后用 .val() 设：
     * areaOptions / categoryOptions 拿 selected 参数比的是 ===，而 query 里读出来的是
     * 字符串、item.id 是数字，永远匹配不上。占位项（全部分类 / 全部宿舍楼）是手写的
     * ——那两个函数不生成占位项。 */
    loadDictionaries().then(function (data) {
      $("#filter-category").append(NM.categoryOptions(data.categories, null));
      $("#filter-category").val(query.categoryId || "");

      $("#filter-area").append(NM.areaOptions(data.areas, null));
      $("#filter-area").val(query.areaId || "");
    }, function () {
      // 字典拉不到不影响看列表，筛选项留空即可。
    });

    api.get("/api/products", {
      q: query.q || undefined,
      categoryId: query.categoryId || undefined,
      areaId: query.areaId || undefined,
      condition: query.condition || undefined,
      minPrice: query.minPrice || undefined,
      maxPrice: query.maxPrice || undefined,
      sort: query.sort || undefined,
      page: query.page || 1,
      pageSize: PAGE_SIZE
    }).then(function (page) {
      $("#product-list").html(
        gridHtml(page) + NM.pagerHtml(page, function (target) { return listHref(query, target); }));
    }, function (error) {
      $("#product-list").html(NM.errorCard(error));
    });
  }

  /* 把表单里填的读成一份查询对象。空字符串一律丢掉，让 URL 干净。 */
  function readFilters() {
    var query = {};

    function put(key, value) {
      if (value !== "" && value != null) {
        query[key] = value;
      }
    }

    put("q", $("#filter-q").val().trim());
    put("categoryId", $("#filter-category").val());
    put("areaId", $("#filter-area").val());
    put("condition", $("#filter-condition").val());
    put("minPrice", $("#filter-min").val());
    put("maxPrice", $("#filter-max").val());

    var sort = $("#filter-sort").val();

    if (sort && sort !== "Newest") {
      query.sort = sort;
    }

    return query;
  }

  function submitSearch() {
    // 换筛选条件等于换一次查询，页码必须回到第一页。
    location.hash = listHref(readFilters(), 1);
  }

  /* ---------- 详情 ---------- */

  /* 图片区。自己的商品可以删图、加图；别人的只显示。 */
  function galleryHtml(product, isOwner) {
    var images = product.images || [];

    var figures = images.map(function (image) {
      /* srcset 让手机拿 640 那张、桌面拿 1280 那张。不写的话每个视口都会下 1280，
       * 而手机上这个格子只有屏幕一半宽，多出来的像素谁都看不见。mediumUrl 缺失时
       * （老数据）退回到单个 src，srcset 整个不写。 */
      var source = image.mediumUrl
        ? ' srcset="' + NM.esc(image.mediumUrl) + ' 640w, ' + NM.esc(image.url) + ' 1280w"' +
          ' sizes="(max-width: 720px) 50vw, 200px"'
        : '';

      /* data-full 和 src 现在是同一个值，但故意不读 src：格子的 src 是给 srcset 当兜底的，
       * 以后要把格子降成 mediumUrl 省流量时，浮层应该仍然给 1280 那张。读 src 的话
       * 那次改动会静默地把浮层一起降级。 */
      return '<figure class="gallery-item">' +
        '<img src="' + NM.esc(image.url) + '"' + source + ' alt="" loading="lazy"' +
        ' data-action="view-image" data-full="' + NM.esc(image.url) + '">' +
        (isOwner
          ? '<button class="button button-danger gallery-remove" data-action="delete-image" ' +
          'data-image-id="' + image.id + '">删除</button>'
          : '') +
        '</figure>';
    }).join("");

    if (!images.length) {
      figures = '<div class="gallery-empty">还没有图片。' +
        (isOwner ? "发布前至少要有一张。" : "") + '</div>';
    }

    if (!isOwner) {
      return '<div class="gallery">' + figures + '</div>';
    }

    return '<div class="gallery">' + figures + '</div>' +
      '<div class="field">' +
      '<input type="file" id="detail-image-file" accept="image/jpeg,image/png,image/webp">' +
      '<button class="button" data-action="upload-image">上传图片</button>' +
      '</div>' +
      '<div id="detail-image-status"></div>';
  }

  /* 按状态决定给卖家看哪些按钮。三个流转各自只在合法时出现——
   * 让按钮永远可点、点了再报 409，是把状态机的知识推给用户。 */
  function ownerActionsHtml(product) {
    var buttons = [];

    if (product.status === 1 || product.status === 4) {
      buttons.push('<button class="button button-primary" data-action="publish">上架</button>');
    }

    if (product.status === 2) {
      buttons.push('<button class="button" data-action="offline">下架</button>');
      buttons.push('<button class="button" data-action="sold">标记已售出</button>');
    }

    if (product.status === 1 || product.status === 4) {
      buttons.push('<button class="button button-danger" data-action="delete">删除</button>');
    }

    buttons.push('<a class="button" href="#/products/' + product.id + '/edit">编辑</a>');

    return '<div class="action-bar">' + buttons.join("") + '</div>' +
      '<div id="detail-action-status"></div>';
  }

  /* 别人看商品时的那个按钮。会话挂在商品上，所以私信的入口在这里，
   * 私信页里没有「新建会话」——卖家只能回，不能主动开。 */
  function contactHtml(product) {
    return '<div class="action-bar">' +
      '<button class="button button-primary" data-action="contact-seller" ' +
      'data-product-id="' + product.id + '">联系卖家</button>' +
      '</div>';
  }

  function detailHtml(product, isOwner) {
    return '<div class="card">' +
      '<div class="card-head">' +
      '<h1>' + NM.esc(product.title) + '</h1>' +
      NM.statusBadge(product.status) +
      '</div>' +
      '<div class="detail-price">' + price(product.price) + '</div>' +
      '<p class="lede">' + (product.description
        ? NM.esc(product.description).replace(/\n/g, "<br>")
        : "卖家没有写描述。") + '</p>' +
      '<div class="detail-meta">' +
      '<span>分类：' + NM.esc(product.categoryName) + '</span>' +
      '<span>宿舍区：' + NM.esc(product.dormitoryAreaName) + '</span>' +
      '<span>成色：' + NM.esc(NM.conditionText(product.condition)) + '</span>' +
      '<span>卖家：' + NM.esc(product.sellerNickname) + '</span>' +
      '<span>发布于：' + NM.formatDateTime(product.publishedAt || product.createdAt) + '</span>' +
      (product.soldAt ? '<span>售出于：' + NM.formatDateTime(product.soldAt) + '</span>' : '') +
      '</div>' +
      /* 只有详情页给这两个数。列表页不给：那是公开 feed，挨个商品算子查询没必要，
       * 而且"有几个人在问"本来就是点进来才关心的东西。 */
      '<p class="detail-interest">最近一周 ' + product.interestedRecentCount +
      ' 人询问 · 共 ' + product.interestedTotal + ' 人询问</p>' +
      galleryHtml(product, isOwner) +
      /* 浮层放在页面标记里，而不是启动时挂到 body 上：route() 每次换页都整块重建 #view，
       * 放里面等于「换页自动关掉浮层」，不用再写一处收尾。 */
      '<div class="lightbox" id="lightbox" hidden><img alt=""></div>' +
      (isOwner ? ownerActionsHtml(product) : contactHtml(product)) +
      '<p id="detail-error"></p>' +
      '</div>';
  }

  function showDetail(id) {
    $("#view").html(NM.loading());

    api.get("/api/products/" + id).then(function (product) {
      var current = auth.user();
      var isOwner = !!current && current.id === product.sellerId;

      $("#view").html(detailHtml(product, isOwner));
    }, function (error) {
      $("#view").html(NM.errorCard(error));
    });
  }

  /* 流转之后重新拉一次，而不是拿返回值就地改 DOM。
   * 服务端返回的就是新的完整状态，重新渲染是唯一不会和它对不上的做法。 */
  function transition(action) {
    var id = currentProductId();

    $("#detail-action-status").html(NM.loading());

    api.post("/api/products/" + id + "/" + action, null).then(function () {
      showDetail(id);
    }, function (error) {
      $("#detail-action-status").html(NM.inlineError(error));
    });
  }

  function deleteProduct() {
    var id = currentProductId();

    if (!confirm("删除之后不能恢复。确定删除这个商品吗？")) {
      return;
    }

    $("#detail-action-status").html(NM.loading());

    api.del("/api/products/" + id).then(function () {
      location.hash = "#/mine";
    }, function (error) {
      $("#detail-action-status").html(NM.inlineError(error));
    });
  }

  function uploadImage() {
    var id = currentProductId();
    var input = $("#detail-image-file");
    var file = input[0] && input[0].files && input[0].files[0];

    if (!file) {
      $("#detail-image-status").html(NM.inlineError({ message: "先选一张图片。" }));
      return;
    }

    var formData = new FormData();
    formData.append("file", file);

    $("#detail-image-status").html('<div class="loading">上传中… <span id="upload-percent">0%</span></div>');

    api.upload("/api/products/" + id + "/images", formData, function (percent) {
      $("#upload-percent").text(percent + "%");
    }).then(function () {
      showDetail(id);
    }, function (error) {
      $("#detail-image-status").html(NM.inlineError(error));
    });
  }

  function deleteImage(imageId) {
    var id = currentProductId();

    if (!confirm("删除这张图片？")) {
      return;
    }

    api.del("/api/products/" + id + "/images/" + imageId).then(function () {
      showDetail(id);
    }, function (error) {
      $("#detail-error").html(NM.inlineError(error));
    });
  }

  /* ---------- 用标题生成一张封面 ---------- */

  /* 上架要求至少有一张照片（服务端 PublishAsync 里那道检查），但手边没照片的卖家
   * ——二手书、刚拆封的耳机、还没拍的自行车——就发不出去了。所以用标题、成色、价格
   * 和宿舍区在浏览器里画一张方图，当成一张普通照片传上去。
   *
   * 在浏览器画而不是服务端画，是因为中文要字体：服务端得引 SixLabors.Fonts 再往仓库塞
   * 一份可再分发的中文字体，浏览器里字体是现成的。画出来的是真图片，走的是现有上传通道，
   * 后端三档尺寸和缩略图一行都不用改。 */

  /* 和 db/seed-demo.ps1 里的占位图同一套颜色，生成的图和种子数据看起来是一路的。
   * 整张图是纯色块，所以这些值不需要跟主题走。 */
  var COVER_PALETTE = ["#3f6fb5", "#7a4fa3", "#2f8f6f", "#b5543f", "#4a6fa5",
    "#8a6d3b", "#556b8a", "#9c4f6b", "#3d7a7a", "#6b6b3d"];

  /* 标题哈希成一个调色板下标。同一件东西每次颜色一样，不同的东西大概率不一样。
   *
   * 返回值必须落在范围内：越界时 COVER_PALETTE[i] 是 undefined，而 canvas 的 fillStyle
   * 赋 undefined 既不抛错也不生效，只是留着上一次的颜色——图会静默画错。 */
  function coverPaletteIndex(title) {
    var text = String(title || "");
    var hash = 0;

    for (var index = 0; index < text.length; index++) {
      hash = (hash * 31 + text.charCodeAt(index)) % 1000003;
    }

    return hash % COVER_PALETTE.length;
  }

  /* 标题切成最多三行、每行最多八个字，装不下的截断并补省略号。
   * 中文没有词边界，所以按字符切，和 seed 脚本里"超过十个字切两行"是一个思路。 */
  var COVER_LINE_CHARS = 8;
  var COVER_MAX_LINES = 3;

  function coverLines(title) {
    var text = String(title || "");
    var lines = [];

    for (var start = 0; start < text.length && lines.length < COVER_MAX_LINES; start += COVER_LINE_CHARS) {
      lines.push(text.substr(start, COVER_LINE_CHARS));
    }

    if (!lines.length) {
      return [""];
    }

    /* 还有没画完的，把最后一行收拾成"……"结尾。末行本身可能已经满了八个字，
     * 那就让掉最后一个字给省略号，不然它会顶出右边框。 */
    if (lines.length * COVER_LINE_CHARS < text.length) {
      var last = lines[lines.length - 1];

      lines[lines.length - 1] = last.slice(0, COVER_LINE_CHARS - 1) + "…";
    }

    return lines;
  }

  var COVER_EDGE = 800;
  var COVER_PADDING = 70;
  var COVER_FONT_STACK = '"Microsoft YaHei", "PingFang SC", "Hiragino Sans GB", sans-serif';

  function coverFont(size, bold) {
    return (bold ? "bold " : "") + size + "px " + COVER_FONT_STACK;
  }

  /* 画一张 800x800 的 PNG，返回一个 Deferred，resolve 出来的是 Blob。
   *
   * 包一层是因为 canvas.toBlob 是回调式的，而调用点全在用 .then 接的 promise 链。
   * 尺寸取 800 是照 seed 脚本的占位图来的：小于 1280 那一档，所以大图不会被放大。 */
  function drawCover(title, price, condition, area) {
    var deferred = $.Deferred();
    var canvas = document.createElement("canvas");
    var context = canvas.getContext("2d");

    canvas.width = COVER_EDGE;
    canvas.height = COVER_EDGE;

    var inner = COVER_EDGE - COVER_PADDING * 2;

    context.fillStyle = COVER_PALETTE[coverPaletteIndex(title)];
    context.fillRect(0, 0, COVER_EDGE, COVER_EDGE);

    /* 一圈压暗的内框，和 seed 脚本里那道 FromArgb(55, 0, 0, 0) 的边一样，
     * 让纯色块看起来是张图而不是一块漏出来的背景。 */
    context.strokeStyle = "rgba(0, 0, 0, 0.22)";
    context.lineWidth = 6;
    context.strokeRect(3, 3, COVER_EDGE - 7, COVER_EDGE - 7);

    context.textAlign = "center";
    context.textBaseline = "middle";

    var lines = coverLines(title);
    var titleSize = 76;

    /* 全角字比半角宽得多，八个全角字在 76px 下会顶到内框。量一下最宽的那行，
     * 按需要把整块缩小——只缩不小，短标题不会撑成大号字。 */
    context.font = coverFont(titleSize, true);

    lines.forEach(function (line) {
      var width = context.measureText(line).width;

      if (width > inner) {
        titleSize = Math.floor(titleSize * inner / width);
        context.font = coverFont(titleSize, true);
      }
    });

    var titleLineHeight = Math.round(titleSize * 1.2);
    var priceSize = 58;
    var metaSize = 34;

    /* 三块内容（标题、价格、脚注）当成一整摞垂直居中，不各自固定 y——
     * 一行标题和两行标题的观感差太多，居中之后才都站得住。 */
    var blockHeight = lines.length * titleLineHeight + 30 + priceSize + 60 + metaSize;
    var y = (COVER_EDGE - blockHeight) / 2 + titleLineHeight / 2;

    context.fillStyle = "rgba(255, 255, 255, 0.96)";
    context.font = coverFont(titleSize, true);

    lines.forEach(function (line) {
      context.fillText(line, COVER_EDGE / 2, y);
      y += titleLineHeight;
    });

    y += 30 + priceSize / 2;
    context.fillStyle = "rgba(255, 255, 255, 0.9)";
    context.font = coverFont(priceSize, true);
    context.fillText(NM.formatPrice(price), COVER_EDGE / 2, y);

    y += priceSize / 2 + 60 + metaSize / 2;
    context.fillStyle = "rgba(255, 255, 255, 0.75)";
    context.font = coverFont(metaSize, false);
    context.fillText(NM.conditionText(condition) + (area ? " · " + area : ""), COVER_EDGE / 2, y);

    /* 导出成 PNG 而不是 JPEG：整张图是纯色块加文字，PNG 压得更小，而且不会在字边上糊出噪点。 */
    canvas.toBlob(function (blob) {
      if (blob) {
        deferred.resolve(blob);
      } else {
        deferred.reject({ message: "这张图没画出来，换一张浏览器或者直接传照片吧。" });
      }
    }, "image/png");

    return deferred.promise();
  }

  /* 已经生成但还没上传的那张。uploadAll 会把它捎上。 */
  var generatedCover = null;
  var coverPreviewUrl = null;

  function generateCover() {
    var body = readForm();

    if (!body.title) {
      $("#cover-slot").html(NM.inlineError({ message: "先填标题，这张图就是用标题画的。" }));
      return;
    }

    /* 宿舍区读的是下拉框里那一项的文字。字典还没拉回来时是空的，那就先不写这一行。 */
    var area = $("#form-area option:selected").text();

    $("#cover-slot").html(NM.loading());

    drawCover(body.title, body.price, body.condition, area).then(function (blob) {
      generatedCover = blob;

      if (coverPreviewUrl) {
        URL.revokeObjectURL(coverPreviewUrl);
      }

      coverPreviewUrl = URL.createObjectURL(blob);

      $("#cover-slot").html(
        '<img class="cover-preview" src="' + coverPreviewUrl + '" alt="生成的封面预览">' +
        '<p class="hint">上架时会把它当成一张普通照片传上去，和手选的照片一样。</p>' +
        '<button type="button" class="button" data-action="discard-cover">不用了</button>');

      $("#generate-cover").text("重新生成");
    }, function (error) {
      $("#cover-slot").html(NM.inlineError(error));
    });
  }

  function discardCover() {
    generatedCover = null;

    if (coverPreviewUrl) {
      URL.revokeObjectURL(coverPreviewUrl);
      coverPreviewUrl = null;
    }

    $("#cover-slot").empty();
    $("#generate-cover").text("没有照片？用标题生成一张");
  }

  /* 当前页面的商品 id。从 hash 里读，不靠闭包——事件是委托的，
   * 处理函数不记得是哪个页面渲染的它。 */
  function currentProductId() {
    var match = /^#\/products\/(\d+)/.exec(location.hash);

    return match ? match[1] : null;
  }

  /* ---------- 看图浮层 ---------- */

  /* 格子是 200px 方形裁切，点开看同比例的整张——浮层里显示的 url 就是 1280 那张
   * （ImageStorage 按 ResizeMode.Max 写的，比例没动）。原图存储里没有，也不需要有。 */
  function openImage(url) {
    $("#lightbox").removeAttr("hidden").find("img").attr("src", url);
  }

  /* 关掉时清 src，不只是藏起来：留着的话那张 1280 的图会一直被浏览器攥着，
   * 而它在隐藏状态下没有任何用处。
   *
   * 浮层不存在时（没开详情页）$("#lightbox") 是空集，整个函数是空操作，所以调用方
   * 不用先判断浮层开没开。 */
  function closeImage() {
    $("#lightbox").attr("hidden", "hidden").find("img").removeAttr("src");
  }

  /* ---------- 表单（新建与编辑共用） ---------- */

  function formFieldsHtml(product) {
    var value = product || {};

    return '<div class="field-stack">' +
      '<label for="form-title">标题</label>' +
      '<input type="text" id="form-title" maxlength="100" value="' + NM.esc(value.title || "") + '" required>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="form-description">描述</label>' +
      '<textarea id="form-description" rows="5" maxlength="2000">' + NM.esc(value.description || "") + '</textarea>' +
      '<span class="hint">用过多久、有没有磕碰、能不能当面验货。写清楚能少很多来回。</span>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="form-price">价格（元）</label>' +
      '<input type="number" id="form-price" min="0" step="0.01" value="' +
      NM.esc(value.price != null ? value.price : "") + '" required>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="form-condition">成色</label>' +
      '<select id="form-condition">' + NM.conditionOptions(value.condition || 1) + '</select>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="form-category">分类</label>' +
      '<select id="form-category"></select>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="form-area">宿舍区</label>' +
      '<select id="form-area"></select>' +
      '</div>';
  }

  function fillSelects(product) {
    loadDictionaries().then(function (data) {
      $("#form-category").html(NM.categoryOptions(data.categories, product ? product.categoryId : null));
      $("#form-area").html(NM.areaOptions(data.areas, product ? product.dormitoryAreaId : null));
    }, function (error) {
      $("#form-error").html(NM.inlineError(error));
    });
  }

  function readForm() {
    return {
      title: $("#form-title").val().trim(),
      description: $("#form-description").val().trim() || null,
      price: Number($("#form-price").val()),
      condition: Number($("#form-condition").val()),
      categoryId: Number($("#form-category").val()),
      dormitoryAreaId: Number($("#form-area").val())
    };
  }

  /* 提交前先挡一道，规则和服务端的 DataAnnotations 对齐。
   * 服务端仍然会再验一次，这里只是省一次往返。 */
  function validate(body) {
    if (!body.title) { return "标题不能为空。"; }
    if (!isFinite(body.price) || body.price < 0) { return "价格要填一个不小于 0 的数字。"; }
    if (!body.categoryId) { return "请选择分类。"; }
    if (!body.dormitoryAreaId) { return "请选择宿舍区。"; }

    return null;
  }

  /* ---------- 新建（三步） ---------- */

  /* 建商品是三步，一步都省不掉：product_images.product_id 非空且有级联外键，
   * 图片不能在商品之前存在。所以这里第一次提交就立刻把草稿建出来并把 id 留下，
   * 后面两步失败都能原地重试，不会丢掉已经填好的内容。 */
  var draft = null;

  function showCreate() {
    draft = null;
    generatedCover = null;

    $("#view").html(
      '<div class="card form-card">' +
      '<div class="card-head"><h1>发布商品</h1>' +
      '<a class="button" href="#/products">取消</a></div>' +
      '<ol class="steps">' +
      '<li>填写信息</li><li>上传图片</li><li>上架</li>' +
      '</ol>' +
      '<form id="create-form" novalidate>' +
      formFieldsHtml(null) +
      '<div class="field-stack">' +
      '<label for="create-images">图片</label>' +
      '<input type="file" id="create-images" accept="image/jpeg,image/png,image/webp" multiple>' +
      '<span class="hint">JPEG / PNG / WebP，每张不超过 5 MB，最多 9 张。至少一张才能上架。</span>' +
      // type="button" 不能省，不然这个按钮会当成"创建并上架"提交。
      '<button type="button" class="button" id="generate-cover">没有照片？用标题生成一张</button>' +
      '<div id="cover-slot"></div>' +
      '</div>' +
      '<div id="form-error"></div>' +
      '<button class="button button-primary" type="submit" id="create-submit">创建并上架</button>' +
      '</form>' +
      '</div>');

    fillSelects(null);
  }

  function submitCreate() {
    var body = readForm();
    var problem = validate(body);

    if (problem) {
      $("#form-error").html(NM.inlineError({ message: problem }));
      return;
    }

    $("#form-error").empty();
    $("#create-submit").prop("disabled", true).text("处理中…");

    // 草稿已经建好就跳过第一步，直接从没传完的图接着走——重试时不会多建一个商品。
    var creating = draft
      ? $.Deferred().resolve(draft).promise()
      : api.post("/api/products", body).then(function (created) { draft = created; return created; });

    creating.then(function (product) {
      return uploadAll(product.id);
    }).then(function () {
      return api.post("/api/products/" + draft.id + "/publish", null);
    }).then(function () {
      location.hash = "#/products/" + draft.id;
    }, function (error) {
      $("#create-submit").prop("disabled", false).text("重试");

      var suffix = draft
        ? '<p class="muted">草稿已经建好了（ID ' + draft.id + '），修好之后点重试即可，不用重新填。</p>'
        : "";

      $("#form-error").html(NM.inlineError(error) + suffix);
    });
  }

  /* 逐张上传。接口一次只收一张，所以这里排着队发；进度按整体算，
   * 不然九张图会看到九个独立的 0%→100%。 */
  function uploadAll(productId) {
    var input = $("#create-images");
    var files = input.length && input[0].files ? Array.prototype.slice.call(input[0].files) : [];

    // 生成的封面排在最后：SortOrder 由服务端按现有张数接着往下发，所以第一张是封面时
    // 仍然是手选的照片，只有一张照片都没选时这张才轮到 SortOrder = 0。
    if (generatedCover) {
      files.push(new File([generatedCover], "cover.png", { type: "image/png" }));
    }

    if (!files.length) {
      return $.Deferred().resolve().promise();
    }

    var done = 0;

    function next() {
      if (done >= files.length) {
        return $.Deferred().resolve().promise();
      }

      var index = done;
      var formData = new FormData();

      formData.append("file", files[index]);

      return api.upload("/api/products/" + productId + "/images", formData, function (percent) {
        var overall = Math.round((index + percent / 100) / files.length * 100);

        $("#create-submit").text("上传图片 " + overall + "%");
      }).then(function () {
        done += 1;
        return next();
      });
    }

    return next();
  }

  /* ---------- 编辑 ---------- */

  function showEdit(id) {
    $("#view").html(NM.loading());

    api.get("/api/products/" + id).then(function (product) {
      var current = auth.user();

      // 编辑页只有本人能用。服务端也会拒，这里先拦一道，免得让人白填一遍。
      if (!current || current.id !== product.sellerId) {
        $("#view").html('<div class="card state-card"><h1>不能编辑</h1>' +
          '<p class="muted">这不是你发布的商品。</p><a class="button" href="#/products">回到列表</a></div>');
        return;
      }

      $("#view").html(
        '<div class="card form-card">' +
        '<div class="card-head"><h1>编辑商品</h1>' +
        '<a class="button" href="#/products/' + product.id + '">取消</a></div>' +
        '<form id="edit-form" novalidate>' +
        formFieldsHtml(product) +
        '<div id="form-error"></div>' +
        '<button class="button button-primary" type="submit">保存</button>' +
        '</form>' +
        '</div>');

      fillSelects(product);
    }, function (error) {
      $("#view").html(NM.errorCard(error));
    });
  }

  function submitEdit(id) {
    var body = readForm();
    var problem = validate(body);

    if (problem) {
      $("#form-error").html(NM.inlineError({ message: problem }));
      return;
    }

    $("#form-error").empty();

    api.put("/api/products/" + id, body).then(function () {
      location.hash = "#/products/" + id;
    }, function (error) {
      $("#form-error").html(NM.inlineError(error));
    });
  }

  /* ---------- 我的商品 ---------- */

  function showMine(query) {
    $("#view").html(
      '<div class="card">' +
      '<div class="card-head"><h2>我的商品</h2>' +
      '<a class="button button-primary" href="#/products/new">发布商品</a></div>' +
      '<div id="mine-list">' + NM.loading() + '</div>' +
      '</div>');

    api.get("/api/products/mine", {
      page: query.page || 1,
      pageSize: PAGE_SIZE
    }).then(function (page) {
      $("#mine-list").html(
        gridHtml(page) +
        NM.pagerHtml(page, function (target) { return "#/mine?page=" + target; }));
    }, function (error) {
      $("#mine-list").html(NM.errorCard(error));
    });
  }

  return {
    showList: showList,
    submitSearch: submitSearch,
    showDetail: showDetail,
    showCreate: showCreate,
    submitCreate: submitCreate,
    showEdit: showEdit,
    submitEdit: submitEdit,
    showMine: showMine,
    transition: transition,
    deleteProduct: deleteProduct,
    uploadImage: uploadImage,
    deleteImage: deleteImage,
    generateCover: generateCover,
    discardCover: discardCover,
    openImage: openImage,
    closeImage: closeImage
  };
})(jQuery);
