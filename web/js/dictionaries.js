/* 分类与宿舍区两本字典的管理页。原本只是两张只读的表，长在 app.js 里；
 * 现在多了增删改，也多了「只有管理员能进」这道门，单独一个文件放。
 *
 * 「普通用户看不到」只做在界面上：导航那两条链接和首页那两张入口卡按角色隐藏，
 * 直接敲 #/categories 会看到一张「没有权限」的卡。接口那边的 GET 仍然是匿名的，
 * 因为商品搜索栏的分类和宿舍区下拉全靠它——这一点在 web/README.md 里也写了。 */
window.dictionaries = (function ($) {
  "use strict";

  var NM = window.NM;

  /* 分类树一次拉满：接口分页、树不分页，按 MaxPageSize 拉 100 条够用。 */
  var CATEGORY_PAGE_SIZE = 100;
  var AREA_PAGE_SIZE = 20;

  /* 父分类下拉的占位项。写在这里是因为它出现两次：表单刚渲染出来时和每次重填选项时。 */
  var TOP_LEVEL_OPTION = '<option value="">无（顶级分类）</option>';

  /* 当前这一屏的数据和编辑状态。编辑时要拿它们重画表单，所以留在模块里而不是闭包里——
   * 事件是委托在 document 上的，处理函数不记得是哪个页面渲染的它。 */
  var currentCategories = [];
  var currentEdit = null;
  var areaQuery = {};

  /* 是不是管理员。role 是 users 表里读出来的，Admin = 2。
   * 这里只决定画不画，真正的判定在服务端。 */
  function isAdmin(current) {
    return !!current && current.role === 2;
  }

  function forbiddenHtml() {
    return '<div class="card state-card"><h1>没有权限</h1>' +
      '<p class="muted">分类和宿舍区只有管理员能改。</p>' +
      '<a class="button" href="#/products">去逛商品</a></div>';
  }

  /* 门禁。必须等 /me 回来才算数：本地缓存里那份 role 可能是提权之前的，
   * 改完 role 直接刷新页面就会撞上。这两个页面访问很少，多发一次请求换掉这个坑划算。 */
  function gate(render) {
    auth.refresh().then(function (current) {
      if (!isAdmin(current)) {
        $("#view").html(forbiddenHtml());
        return;
      }

      render();
    });
  }

  /* ---------- 分类 ---------- */

  /* 扁平列表按 parentId 递归成嵌套的 <ul>。接口最多给 100 条，够用。
   * 结构（.tree / .tree-child / .tree-node / .tree-name）不能改：
   * out/check-frontend.js 的 shape() 断言认的就是这几个类名。 */
  function treeHtml(items, parentId, depth) {
    var children = items.filter(function (item) {
      return item.parentId === parentId;
    });

    if (!children.length) {
      return "";
    }

    return '<ul class="' + (depth ? "tree-child" : "tree") + '">' +
      children.map(function (item) {
        return '<li>' +
          '<div class="tree-node">' +
          '<span class="tree-name">' + NM.esc(item.name) + '</span>' +
          '<span class="tree-meta">ID ' + item.id + ' · 排序 ' + item.sortOrder + '</span>' +
          '<button type="button" class="button" data-action="edit-category" ' +
          'data-category-id="' + item.id + '">编辑</button>' +
          '<button type="button" class="button button-danger" data-action="delete-category" ' +
          'data-category-id="' + item.id + '">删除</button>' +
          '</div>' +
          treeHtml(items, item.id, depth + 1) +
          '</li>';
      }).join("") +
      '</ul>';
  }

  function categoryFormHtml(value) {
    var editing = !!(value && value.id);

    return '<div class="card form-card">' +
      '<h2>' + (editing ? "编辑分类" : "新建分类") + '</h2>' +
      '<form id="category-form" novalidate>' +
      '<input type="hidden" id="category-id" value="' + (editing ? value.id : "") + '">' +
      '<div class="field-stack">' +
      '<label for="category-name">分类名</label>' +
      '<input type="text" id="category-name" maxlength="50" value="' +
      NM.esc(editing ? value.name : "") + '" required>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="category-parent">父分类</label>' +
      '<select id="category-parent">' + TOP_LEVEL_OPTION + '</select>' +
      '<span class="hint">换一个父分类就等于把这一支挪个地方。</span>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="category-sort">排序</label>' +
      '<input type="number" id="category-sort" value="' + (editing ? value.sortOrder : 0) + '">' +
      '<span class="hint">数字小的排前面。</span>' +
      '</div>' +
      '<div id="category-error"></div>' +
      '<div class="action-bar">' +
      '<button class="button button-primary" type="submit">' + (editing ? "保存" : "新建") + '</button>' +
      (editing ? '<button class="button" type="button" data-action="cancel-category">取消</button>' : '') +
      '</div>' +
      '</form></div>';
  }

  /* 父分类下拉。自己不能当自己的父，先从候选里剔掉。
   * 后代没法在这里判（要整棵树），交给服务端拒——那边会回一句中文的 400。
   *
   * 整段 .html() 重写而不是 append：列表刷新会调到这里，但表单不一定同时重画过
   * （删掉一个不是正在编辑的分类就是这种情况），append 的话选项会一次翻一倍。 */
  function fillCategoryParents(value) {
    var editingId = value ? value.id : null;
    var selected = value ? value.parentId : null;

    var candidates = currentCategories.filter(function (item) {
      return item.id !== editingId;
    });

    $("#category-parent")
      .html(TOP_LEVEL_OPTION)
      .append(NM.categoryOptions(candidates, selected));
  }

  function renderCategoryForm(value) {
    currentEdit = value;
    $("#category-form-slot").html(categoryFormHtml(value));
    fillCategoryParents(value);
  }

  function showCategories() {
    $("#view").html(NM.loading());

    gate(function () {
      currentEdit = null;

      $("#view").html(
        '<div id="category-form-slot"></div>' +
        '<div class="card">' +
        '<div class="card-head"><h2>分类字典</h2>' +
        '<button class="button" data-reload>刷新</button></div>' +
        '<div id="list">' + NM.loading() + '</div>' +
        '</div>');

      // 表单先画，父分类下拉要等列表回来才有内容可填。
      $("#category-form-slot").html(categoryFormHtml(null));

      loadCategories();
    });
  }

  function loadCategories() {
    api.get("/api/categories", { page: 1, pageSize: CATEGORY_PAGE_SIZE }).then(function (page) {
      currentCategories = page.items;

      var html = treeHtml(page.items, null, 0);

      $("#list").html((html ? html : NM.empty("还没有分类。")) +
        '<p class="muted">共 ' + page.totalCount + ' 个分类。</p>');

      // 列表回来之后才填下拉：候选就是这一份，而且编辑中的那一项要剔掉。
      fillCategoryParents(currentEdit);
    }, function (error) {
      $("#list").html(NM.errorCard(error));
    });
  }

  function editCategory(id) {
    var found = currentCategories.filter(function (item) {
      return String(item.id) === String(id);
    })[0];

    if (found) {
      renderCategoryForm(found);
    }
  }

  function cancelCategory() {
    renderCategoryForm(null);
  }

  function submitCategory() {
    var id = $("#category-id").val();
    var name = $("#category-name").val().trim();

    if (!name) {
      $("#category-error").html(NM.inlineError({ message: "请填分类名。" }));
      return;
    }

    var parent = $("#category-parent").val();

    var body = {
      name: name,
      // 下拉的空值表示顶级分类，接口那边是 null。
      parentId: parent ? Number(parent) : null,
      sortOrder: parseInt($("#category-sort").val(), 10) || 0
    };

    $("#category-error").empty();

    var request = id
      ? api.put("/api/categories/" + id, body)
      : api.post("/api/categories", body);

    request.then(function () {
      renderCategoryForm(null);
      loadCategories();
    }, function (error) {
      $("#category-error").html(NM.inlineError(error));
    });
  }

  function deleteCategory(id) {
    if (!confirm("删除之后不能恢复。确定删掉这个分类吗？")) {
      return;
    }

    api.del("/api/categories/" + id).then(function () {
      // 删掉的正好是编辑中的那个，表单得退回新建态。
      if (currentEdit && String(currentEdit.id) === String(id)) {
        renderCategoryForm(null);
      }

      loadCategories();
    }, function (error) {
      // 有子分类或还有商品在用时会走到这里，服务端回的是中文的 409，直接显示。
      $("#list").prepend(NM.inlineError(error));
    });
  }

  /* ---------- 宿舍区 ---------- */

  function areaFormHtml(value) {
    var editing = !!(value && value.id);

    return '<div class="card form-card">' +
      '<h2>' + (editing ? "编辑宿舍区" : "新建宿舍区") + '</h2>' +
      '<form id="area-form" novalidate>' +
      '<input type="hidden" id="area-id" value="' + (editing ? value.id : "") + '">' +
      '<div class="field-stack">' +
      '<label for="area-name">宿舍区名</label>' +
      '<input type="text" id="area-name" maxlength="50" value="' +
      NM.esc(editing ? value.name : "") + '" required>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="area-sort">排序</label>' +
      '<input type="number" id="area-sort" value="' + (editing ? value.sortOrder : 0) + '">' +
      '<span class="hint">数字小的排前面，商品搜索栏的下拉也按它排。</span>' +
      '</div>' +
      '<div id="area-error"></div>' +
      '<div class="action-bar">' +
      '<button class="button button-primary" type="submit">' + (editing ? "保存" : "新建") + '</button>' +
      (editing ? '<button class="button" type="button" data-action="cancel-area">取消</button>' : '') +
      '</div>' +
      '</form></div>';
  }

  function showDormitoryAreas(query) {
    areaQuery = query || {};
    $("#view").html(NM.loading());

    gate(function () {
      currentEdit = null;

      $("#view").html(
        '<div id="area-form-slot">' + areaFormHtml(null) + '</div>' +
        '<div class="card">' +
        '<div class="card-head"><h2>宿舍区字典</h2>' +
        '<button class="button" data-reload>刷新</button></div>' +
        '<div id="list">' + NM.loading() + '</div>' +
        '</div>');

      loadAreas();
    });
  }

  function loadAreas() {
    api.get("/api/dormitory-areas", {
      page: areaQuery.page || 1,
      pageSize: AREA_PAGE_SIZE
    }).then(function (result) {
      if (!result.items.length) {
        $("#list").html(NM.empty("这一页没有数据。"));
        return;
      }

      $("#list").html(
        '<div class="table-scroll"><table><thead><tr>' +
        '<th>ID</th><th>名称</th><th class="num">排序</th><th>创建时间</th><th>更新时间</th><th>操作</th>' +
        '</tr></thead><tbody>' +
        result.items.map(function (area) {
          return '<tr>' +
            '<td>' + area.id + '</td>' +
            '<td>' + NM.esc(area.name) + '</td>' +
            '<td class="num">' + area.sortOrder + '</td>' +
            '<td>' + NM.formatDateTime(area.createdAt) + '</td>' +
            '<td>' + NM.formatDateTime(area.updatedAt) + '</td>' +
            '<td>' +
            '<button type="button" class="button" data-action="edit-area" ' +
            'data-area-id="' + area.id + '">编辑</button> ' +
            '<button type="button" class="button button-danger" data-action="delete-area" ' +
            'data-area-id="' + area.id + '">删除</button>' +
            '</td>' +
            '</tr>';
        }).join("") +
        '</tbody></table></div>' +
        NM.pagerHtml(result, function (target) { return "#/dormitory-areas?page=" + target; }));
    }, function (error) {
      $("#list").html(NM.errorCard(error));
    });
  }

  /* 编辑宿舍区不用再拉一次接口：列表里那几行的字段就是表单要的全部内容。
   * 从 DOM 上读，比在模块里再存一份当前页的数据省事，也不会两份对不上。 */
  function editArea(id) {
    var row = $("[data-action='edit-area'][data-area-id='" + id + "']").closest("tr");

    if (!row.length) {
      return;
    }

    var cells = row.find("td");

    currentEdit = {
      id: Number(id),
      name: cells.eq(1).text(),
      sortOrder: parseInt(cells.eq(2).text(), 10) || 0
    };

    $("#area-form-slot").html(areaFormHtml(currentEdit));
  }

  function cancelArea() {
    currentEdit = null;
    $("#area-form-slot").html(areaFormHtml(null));
  }

  function submitArea() {
    var id = $("#area-id").val();
    var name = $("#area-name").val().trim();

    if (!name) {
      $("#area-error").html(NM.inlineError({ message: "请填宿舍区名。" }));
      return;
    }

    var body = {
      name: name,
      sortOrder: parseInt($("#area-sort").val(), 10) || 0
    };

    $("#area-error").empty();

    var request = id
      ? api.put("/api/dormitory-areas/" + id, body)
      : api.post("/api/dormitory-areas", body);

    request.then(function () {
      cancelArea();
      loadAreas();
    }, function (error) {
      $("#area-error").html(NM.inlineError(error));
    });
  }

  function deleteArea(id) {
    if (!confirm("删除之后不能恢复。确定删掉这个宿舍区吗？")) {
      return;
    }

    api.del("/api/dormitory-areas/" + id).then(function () {
      cancelArea();
      loadAreas();
    }, function (error) {
      $("#list").prepend(NM.inlineError(error));
    });
  }

  return {
    showCategories: showCategories,
    showDormitoryAreas: showDormitoryAreas,
    submitCategory: submitCategory,
    editCategory: editCategory,
    cancelCategory: cancelCategory,
    deleteCategory: deleteCategory,
    submitArea: submitArea,
    editArea: editArea,
    cancelArea: cancelArea,
    deleteArea: deleteArea
  };
})(jQuery);
