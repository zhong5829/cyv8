// ========== 全局状态 ==========
const state = {
  me: null,             // 登录用户信息 { operator, deptNo, role, permissions }
  details: [],          // 销售明细列表
  selectedClient: null, // 选中的客户 { gno, gname }
  operators: [],        // 操作员列表（登录下拉 + 查询筛选共用）
  queryPage: 1,
};

const TOKEN_KEY = 'qpgl_token';

// ========== 工具函数 ==========
const $ = (id) => document.getElementById(id);
const API = (path) => `/api${path}`;
const fmt = (n) => n == null ? '0.00' : Number(n).toFixed(2);
// 数量类列统一显示口径：整数向零截断（对齐旧系统 VFP INT()，如 -2.6 -> -2，而非四舍五入/向下取整）。
// 仅用于显示；库存拦截、合计金额等比较与计算必须使用原始数值。
// 金额列仍用 fmt（两位小数），比率/重量/体积等非件数列不走此函数。
const fmtQty = (n, empty = '-') => {
  if (n === null || n === undefined || n === '') return empty;
  const v = Number(n);
  if (!Number.isFinite(v)) return empty;
  return String(Math.trunc(v));
};

// ========== 数量输入口径（2026-11-07：全部整数卖出，不做静默截断）==========
// 仅受理「纯数字整数串」；'2.60'、'-2'、'+2'、'2e3'、空串 一律判非法。
// 输入阶段拦掉小数点/符号，粘贴整段校验，确认与提交时再各校验一次。
const QTY_MAX = 9999999;   // 旧系统数量列 INPUTMASK="9999999"（最多 7 位）
const isQtyInteger = (raw) => /^[0-9]+$/.test(String(raw ?? '').trim());

function showToast(msg, type = '') {
  const t = $('toast');
  t.textContent = msg; t.className = `toast show ${type}`;
  clearTimeout(t._timer);
  t._timer = setTimeout(() => t.classList.remove('show'), 2500);
}

function escHtml(s) { if (s == null) return ''; return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }
function escAttr(s) { return String(s == null ? '' : s).replace(/'/g,"&#39;").replace(/"/g,'&quot;'); }
function escapeAttr(s) { return String(s == null ? '' : s).replace(/\\/g,'\\\\').replace(/'/g,"\\'"); }
function formatDate(d) {
  if (!d) return '-';
  if (typeof d === 'string') return d.slice(0, 10);
  try { return new Date(d).toISOString().slice(0, 10); } catch { return '-'; }
}

function debounce(fn, ms) {
  let timer;
  return (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
}

// ========== 认证 ==========
function getAuthToken() { return localStorage.getItem(TOKEN_KEY) || ''; }
function saveAuthToken(token) { localStorage.setItem(TOKEN_KEY, token); }

async function authFetch(url, options = {}) {
  const token = getAuthToken();
  if (token) {
    options.headers = options.headers || {};
    options.headers['Authorization'] = `Bearer ${token}`;
  }
  const res = await fetch(url, options);

  // 401 → Token 过期或无效，跳转登录
  if (res.status === 401) {
    logout();
    throw new Error('未登录或登录已过期');
  }
  return res;
}

/** 解析 ApiResponse：失败抛错（提示 error.message） */
async function readApi(res, fallbackMsg) {
  let json = null;
  try { json = await res.json(); } catch {}
  if (!res.ok || (json && json.ok === false)) {
    const msg = json?.error?.message || json?.error?.code || fallbackMsg || `HTTP ${res.status}`;
    throw new Error(msg);
  }
  return json?.data !== undefined ? json.data : json;
}

function logout() {
  localStorage.removeItem(TOKEN_KEY);
  state.me = null;
  $('loginOverlay').classList.remove('hidden');
  $('loginError').style.display = 'none';
  $('loginPwd').value = '';
  $('opBadge').style.display = 'none';
  $('btnLogout').style.display = 'none';
  loadLoginUsers();
}

function hideLoginOverlay() { $('loginOverlay').classList.add('hidden'); }

async function doLogin() {
  const user = $('loginUser').value.trim();
  const pwd = $('loginPwd').value;
  const errEl = $('loginError');
  const btn = $('btnLogin');

  if (!user) { errEl.textContent = '请选择操作员'; errEl.style.display = 'block'; return; }
  if (!pwd) { errEl.textContent = '请输入密码'; errEl.style.display = 'block'; $('loginPwd').focus(); return; }
  errEl.style.display = 'none';
  btn.disabled = true; btn.textContent = '登录中...';

  try {
    const res = await fetch(API('/auth/login'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Per: user, Password: pwd })
    });
    const data = await readApi(res, '登录失败');
    saveAuthToken(data.token);
    state.me = data;
    await afterLogin();
  } catch (e) {
    errEl.textContent = e.message === '网络错误' || e.name === 'TypeError' ? '网络错误，请检查连接' : e.message;
    errEl.style.display = 'block';
    $('loginPwd').value = '';
    $('loginPwd').focus();
  } finally {
    btn.disabled = false; btn.textContent = '登 录';
  }
}

/** 登录成功后的初始化 */
async function afterLogin() {
  hideLoginOverlay();
  $('opBadge').textContent = `${state.me.operator}（部门 ${state.me.deptNo || '-'}）`;
  $('opBadge').style.display = '';
  $('btnLogout').style.display = '';
  showToast(`欢迎, ${state.me.operator}`, 'success');
  if (!state.me.deptNo) {
    showToast('当前账号无部门信息，配件查询不可用', 'error');
  }
  await loadOperators();
  setDefaultDates();
}

/** 加载操作员列表（登录下拉 + 查询筛选共用，缓存10分钟） */
async function loadOperators() {
  const sel = $('qPer');
  if (state.operators.length) fillOperatorSelects();
  try {
    const res = await fetch(API('/auth/operators'));
    if (!res.ok) return;
    const data = await readApi(res);
    if (Array.isArray(data)) {
      state.operators = data;
      sessionStorage.setItem('qpgl_operators', JSON.stringify(data));
      fillOperatorSelects();
    }
  } catch {}
}

function fillOperatorSelects() {
  const sel = $('qPer');
  const cur = sel.value;
  sel.innerHTML = '<option value="">全部</option>' +
    state.operators.map(o => `<option value="${escAttr(o)}">${escHtml(o)}</option>`).join('');
  sel.value = cur;
}

async function loadLoginUsers() {
  const sel = $('loginUser');
  try {
    const res = await fetch(API('/auth/operators'));
    if (!res.ok) return;
    const data = await readApi(res);
    const users = Array.isArray(data) ? data : [];
    if (users.length > 0) {
      sel.innerHTML = '<option value="">-- 请选择 --</option>' +
        users.map(u => `<option value="${escAttr(u)}">${escHtml(u)}</option>`).join('');
      sel.selectedIndex = 1;
    }
    $('loginPwd').focus();
  } catch {}
}

// ========== Tab 切换 ==========
document.querySelectorAll('.tab-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    closeAllModals();
    document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
    document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
    btn.classList.add('active');
    $(`tab-${btn.dataset.tab}`).classList.add('active');
  });
});

function closeAllModals() {
  $('partDialog')?.classList.remove('show');
  $('billDetailModal')?.classList.remove('show');
  $('historyDocModal')?.classList.remove('show');
  $('confirmModal')?.classList.remove('show');
  $('clientDropdown')?.classList.remove('show');
}

// ========== 初始化 ==========
async function init() {
  bindEvents();
  bindModalCloseOnBackdrop();

  // 恢复缓存的操作员列表（登录下拉）
  try {
    const cached = sessionStorage.getItem('qpgl_operators');
    if (cached) { state.operators = JSON.parse(cached); fillOperatorSelects(); }
  } catch {}

  const token = getAuthToken();
  if (token) {
    // 验证 Token 是否仍然有效（/auth/me）
    try {
      const res = await authFetch(API('/auth/me'));
      state.me = await readApi(res);
      await afterLogin();
    } catch {
      logout();
    }
  } else {
    await loadLoginUsers();
    $('loginPwd').addEventListener('keydown', e => { if (e.key === 'Enter') doLogin(); });
  }
}

function setDefaultDates() {
  const today = new Date().toISOString().slice(0, 10);
  $('qStart').value = today;
  $('qEnd').value = today;
}

// ========== 事件绑定 ==========
function bindEvents() {
  ['qPartNo', 'qPartName', 'qCartype'].forEach(id => {
    $(id).addEventListener('input', debounce(searchParts, 400));
    $(id).addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); searchParts(); } });
  });

  $('clientInput').addEventListener('input', debounce(searchClients, 350));
  $('clientInput').addEventListener('focus', () => {
    if ($('clientInput').value.trim()) searchClients();
  });

  $('btnAddDetail').addEventListener('click', addDetailFromDialog);
  $('btnSubmit').addEventListener('click', submitOrder);
  $('btnClear').addEventListener('click', clearAll);
  $('btnQuery').addEventListener('click', () => { state.queryPage = 1; queryBills(); });
  $('qPaper').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); state.queryPage = 1; queryBills(); } });

  // 弹窗内事件：数量→单价→确认
  $('dlgAmount').addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); $('dlgPrice').focus(); $('dlgPrice').select(); }
  });
  $('dlgPrice').addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); addDetailFromDialog(); }
  });
  // 金额实时计算 + 负库存拦截
  bindQtyInputGuards();
  $('dlgPrice').addEventListener('input', updateDlgAmount);
  // 历史筛选防抖（300ms，对齐桌面端）
  $('dlgSellFilter').addEventListener('input', debounce(() => loadSellHistory($('dlgSellFilter').value.trim()), 300));
  $('dlgPurchaseFilter').addEventListener('input', debounce(() => loadPurchaseHistory($('dlgPurchaseFilter').value.trim()), 300));

  // 整单确认：优惠试算 + 确认提交
  $('cfYhl').addEventListener('input', recalcConfirm);
  $('cfYhlType').addEventListener('change', recalcConfirm);
  $('btnConfirmDoc').addEventListener('click', confirmDoc);

  document.querySelectorAll('input[name="priceType"]').forEach(r => {
    r.addEventListener('change', onPriceTypeChange);
  });
}

function bindModalCloseOnBackdrop() {
  ['partDialog', 'billDetailModal', 'historyDocModal'].forEach(id => {
    const el = $(id);
    if (!el) return;
    el.addEventListener('click', e => {
      if (e.target === el) el.classList.remove('show');
    });
  });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') closeAllModals();
  });
}

// ========== 一、配件搜索（GET /api/parts?dpm=&cp=&na1=&ty=） ==========
async function searchParts() {
  const cp = $('qPartNo').value.trim();
  const na1 = $('qPartName').value.trim();
  const ty = $('qCartype').value.trim();

  if (!cp && !na1 && !ty) {
    $('partsBody').innerHTML = '<tr><td colspan="9" class="empty-hint">输入条件后回车或等待自动搜索...</td></tr>';
    return;
  }
  if (!state.me?.deptNo) { showToast('当前账号无部门信息，无法查询库存', 'error'); return; }

  const params = new URLSearchParams();
  params.set('dpm', state.me.deptNo);
  if (cp) params.set('cp', cp);
  if (na1) params.set('na1', na1);
  if (ty) params.set('ty', ty);
  params.set('page', '1');
  params.set('pageSize', '100');

  try {
    const res = await authFetch(API(`/parts?${params}`));
    const data = await readApi(res);
    renderParts(data.items || []);
  } catch (e) {
    showToast('搜索失败: ' + e.message, 'error');
  }
}

function renderParts(parts) {
  const tbody = $('partsBody');
  if (!parts || parts.length === 0) {
    tbody.innerHTML = '<tr><td colspan="9" class="empty-hint">未找到匹配的配件</td></tr>';
    return;
  }

  tbody.innerHTML = parts.map(p => {
    const stock = p.kcamount ?? 0;
    const rowClass = stock <= 0 ? 'row-nostock' : '';
    return `
    <tr class="${rowClass}" data-part='${escapeAttr(JSON.stringify(p))}' onclick="onPartClick(this)">
      <td>${escHtml(p.cp)}</td>
      <td>${escHtml(p.nno)}</td>
      <td>${escHtml(p.na1)}</td>
      <td>${escHtml(p.ty)}</td>
      <td style="text-align:right">${fmtQty(stock)}</td>
      <td style="text-align:right">${fmt(p.oprc)}</td>
      <td style="text-align:right">${fmt(p.oprb)}</td>
      <td>${escHtml(p.posi)}</td>
      <td>${escHtml(p.depotno)}</td>
    </tr>`;
  }).join('');
}

function onPartClick(row) {
  document.querySelectorAll('#partsBody tr').forEach(r => r.classList.remove('selected'));
  row.classList.add('selected');
  const part = JSON.parse(row.dataset.part);
  openPartDialog(part, -1);
}

// ========== 二、客户搜索（GET /api/partners?keyword=） ==========
async function searchClients() {
  const keyword = $('clientInput').value.trim();
  const dd = $('clientDropdown');

  if (!keyword) { dd.classList.remove('show'); dd.innerHTML = ''; return; }

  try {
    const res = await authFetch(API(`/partners?keyword=${encodeURIComponent(keyword)}&page=1&pageSize=20`));
    const data = await readApi(res);
    renderClientDropdown(data.items || [], dd);
  } catch (e) { showToast('客户搜索失败: ' + e.message, 'error'); }
}

function renderClientDropdown(list, dd) {
  if (!list.length) { dd.classList.remove('show'); return; }
  dd.innerHTML = list.map(c =>
    `<div class="dropdown-item" data-gno="${escAttr(c.partNo)}" data-gname="${escAttr(c.partName)}"
         onclick="selectClient(this)">${escHtml(c.partName)} (${escHtml(c.mobile || c.tel || c.linkMan || '-')})</div>`
  ).join('');
  dd.classList.add('show');
}

function selectClient(el) {
  state.selectedClient = { gno: el.dataset.gno || '', gname: el.dataset.gname || '' };
  $('clientInput').value = el.dataset.gname;
  $('clientDropdown').classList.remove('show');
}

document.addEventListener('click', e => {
  if (!$('clientInput').contains(e.target) && !$('clientDropdown').contains(e.target))
    $('clientDropdown').classList.remove('show');
});

// ========== 三、添加配件弹窗（完全复刻桌面端 SaleLineEditDialog） ==========
// origQty：弹窗打开（或进入编辑）时数量框显示的整数值；数量输入非法时整体回退到这个值
let dlgState = { part: null, editIndex: -1, origQty: '1' };

function openPartDialog(part, editIndex = -1) {
  dlgState.part = part;
  dlgState.editIndex = editIndex;

  const editing = editIndex >= 0 && state.details[editIndex];

  // ---- 销售信息（对齐桌面端左列）----
  $('dlgPno').textContent = part.nno || '';
  $('dlgPname').textContent = part.na1 || '';
  $('dlgPosi').textContent = `${part.posi || '-'} / ${part.depotno || '-'}`;
  $('dlgStock').textContent = `${fmtQty(part.kcamount ?? 0)} ${part.unit || ''}`.trim();
  $('dlgMemo').value = editing ? (editing.remark || '') : '';
  $('dlgCartype').value = editing ? (editing.ty || '') : (part.ty || '');
  $('dlgAmount').value = editing ? editing.qty : '1';
  // 记录「进入编辑前的原值」：数量输入出现小数/非法内容时整体回退到此处，而非只吞字符
  dlgState.origQty = String($('dlgAmount').value ?? '1').trim();
  $('dlgPrice').value = editing ? editing.price : (part.oprc ?? '');
  $('rbRetail').checked = true;

  // ---- 配件信息（对齐桌面端 TxtIpInfo/TxtFaInfo/TxtDepotInfo）----
  $('dlgIpInfo').textContent = `进价（不含税）：${fmt(part.iprj)}　　进价（含税）：${fmt(part.iprc)}`;
  $('dlgFaInfo').textContent = `产地：${part.dc || '-'}　　单位：${part.unit || '-'}　　方案：${part.fa || '-'}`;
  $('dlgDepotInfo').textContent = `内部编号：${part.cp || '-'}　　零售价：${fmt(part.oprc)}　　批发价：${fmt(part.oprb)}`;

  updateDlgAmount();

  // 显示弹窗
  $('partDialog').classList.add('show');

  // ---- 历史加载（对齐桌面端：销售历史默认按开单客户名筛选）----
  const customerName = state.selectedClient?.gname || '';
  $('dlgSellFilter').value = customerName;
  loadSellHistory(customerName);
  $('dlgPurchaseFilter').value = '';
  loadPurchaseHistory('');

  setTimeout(() => { $('dlgAmount').focus(); $('dlgAmount').select(); }, 100);
}

function closePartDialog() {
  $('partDialog').classList.remove('show');
  dlgState.part = null;
  dlgState.editIndex = -1;
}

// ---- 销售历史（对齐桌面端 LoadHistoryAsync：筛选防抖 300ms；新开行自动带出筛选结果第一条最近成交价）----
async function loadSellHistory(keyword) {
  const p = dlgState.part;
  if (!p) return;
  const tbody = $('sellHistoryBody');
  try {
    const params = new URLSearchParams({ top: '0' });
    if (keyword) params.set('keyword', keyword);
    const res = await authFetch(API(`/parts/${encodeURIComponent(p.nno)}/sale-history?${params}`));
    const list = await readApi(res);
    const rows = list || [];

    if (!rows.length) {
      tbody.innerHTML = '<tr><td colspan="6" class="empty-hint-sm">暂无销售记录</td></tr>';
      return;
    }
    tbody.innerHTML = rows.map(h => `
      <tr class="${h.qty < 0 ? 'row-neg' : ''}" ondblclick="openSaleDocLines('${escAttr(h.paper)}', '${escAttr(h.type)}', '${escAttr(h.gname)}', '${escAttr(h.outDate)}')">
        <td>${escHtml(h.paper)}</td>
        <td>${formatDate(h.outDate)}</td>
        <td>${escHtml(h.gname)}</td>
        <td class="ctr">${escHtml(h.depotno)}</td>
        <td style="text-align:right">${fmtQty(h.qty)}</td>
        <td style="text-align:right">${fmt(h.price)}</td>
      </tr>`).join('');

    // 筛选后自动匹配最近价格填充单价框（2026-10-31 需求；2026-11-04 修复：编辑已有行不回填）
    if (rows.length > 0 && dlgState.editIndex < 0) {
      $('dlgPrice').value = rows[0].price;
      updateDlgAmount();
    }
  } catch (e) {
    tbody.innerHTML = `<tr><td colspan="6" class="empty-hint-sm">加载失败</td></tr>`;
  }
}

// ---- 进货历史（对齐桌面端 LoadPurchaseHistoryAsync）----
async function loadPurchaseHistory(keyword) {
  const p = dlgState.part;
  if (!p) return;
  const tbody = $('buyHistoryBody');
  try {
    const params = new URLSearchParams({ top: '0' });
    if (keyword) params.set('keyword', keyword);
    const res = await authFetch(API(`/parts/${encodeURIComponent(p.nno)}/purchase-history?${params}`));
    const rows = (await readApi(res)) || [];

    if (!rows.length) {
      tbody.innerHTML = '<tr><td colspan="6" class="empty-hint-sm">暂无采购记录</td></tr>';
      return;
    }
    tbody.innerHTML = rows.map(h => `
      <tr class="${h.qty < 0 ? 'row-neg' : ''}" ondblclick="openPurchaseDocLines('${escAttr(h.cno)}', '${escAttr(h.gyname)}', '${escAttr(h.inDate)}')">
        <td>${escHtml(h.cno)}</td>
        <td>${formatDate(h.inDate)}</td>
        <td>${escHtml(h.gyname)}</td>
        <td style="text-align:right">${fmtQty(h.qty)}</td>
        <td style="text-align:right">${fmt(h.iprj)}</td>
        <td style="text-align:right">${fmt(h.iprc)}</td>
      </tr>`).join('');
  } catch (e) {
    tbody.innerHTML = `<tr><td colspan="6" class="empty-hint-sm">加载失败</td></tr>`;
  }
}

// ---- 双击销售历史行 → 该销售单整单明细（对齐桌面端 DgSellHistory_MouseDoubleClick；type 用历史行自带值）----
async function openSaleDocLines(paper, type, gname, outDate) {
  showHistoryDoc(`销售单 ${paper} 明细（${gname} ${formatDate(outDate)}）`, '加载中...');
  try {
    const res = await authFetch(API(`/docs/${encodeURIComponent(paper)}?dpm=${encodeURIComponent(state.me.deptNo)}&type=${encodeURIComponent(type || '销售')}`));
    const data = await readApi(res);
    const lines = data.lines || [];
    if (!lines.length) { showHistoryDoc(null, '无明细'); return; }
    showHistoryDoc(null, renderSaleLinesTable(lines));
  } catch (e) {
    showHistoryDoc(null, `加载单据明细失败：${escHtml(e.message)}`);
  }
}

// ---- 双击进货历史行 → 该入库单整单明细（对齐桌面端 DgPurchaseHistory_MouseDoubleClick）----
async function openPurchaseDocLines(cno, gyname, inDate) {
  showHistoryDoc(`入库单 ${cno} 明细（${gyname} ${formatDate(inDate)}）`, '加载中...');
  try {
    const res = await authFetch(API(`/parts/purchase-doc/${encodeURIComponent(cno)}/lines`));
    const lines = (await readApi(res)) || [];
    if (!lines.length) { showHistoryDoc(null, '无明细'); return; }
    showHistoryDoc(null, renderPurchaseLinesTable(lines));
  } catch (e) {
    showHistoryDoc(null, `加载单据明细失败：${escHtml(e.message)}`);
  }
}

function showHistoryDoc(title, bodyHtml) {
  if (title !== null) $('historyDocTitle').textContent = title;
  $('historyDocBody').innerHTML = bodyHtml;
  $('historyDocModal').classList.add('show');
}

function closeHistoryDoc() {
  $('historyDocModal').classList.remove('show');
}

/** 销售单明细表（OutDocLine，列集对齐桌面端 HistoryDocDetailWindow） */
function renderSaleLinesTable(lines) {
  let html = `<div class="table-wrap"><table class="data-table"><thead><tr>
    <th>编号</th><th>内部编号</th><th>名称</th><th>数量</th><th>单价</th><th>金额</th><th>车型</th><th>货位</th><th>备注</th>
  </tr></thead><tbody>`;
  lines.forEach(d => {
    html += `<tr>
      <td>${escHtml(d.cp)}</td><td>${escHtml(d.nno)}</td><td>${escHtml(d.na1)}</td>
      <td style="text-align:center">${fmtQty(d.ckamount)}</td>
      <td style="text-align:right">${fmt(d.oprc)}</td>
      <td style="text-align:right;font-weight:500">${fmt(d.oprct)}</td>
      <td>${escHtml(d.ty)}</td><td>${escHtml(d.posi)}</td><td>${escHtml(d.remark)}</td>
    </tr>`;
  });
  return html + '</tbody></table></div>';
}

/** 入库单明细表（PartPurchaseDocLine） */
function renderPurchaseLinesTable(lines) {
  let html = `<div class="table-wrap"><table class="data-table"><thead><tr>
    <th>内部编号</th><th>名称</th><th>车型</th><th>数量</th><th>进价(含税)</th><th>供应商</th>
  </tr></thead><tbody>`;
  lines.forEach(d => {
    html += `<tr>
      <td>${escHtml(d.nno)}</td><td>${escHtml(d.na1)}</td><td>${escHtml(d.ty)}</td>
      <td style="text-align:center">${fmtQty(d.qty)}</td>
      <td style="text-align:right">${fmt(d.iprc)}</td>
      <td>${escHtml(d.gyname)}</td>
    </tr>`;
  });
  return html + '</tbody></table></div>';
}

// ---- 价格类型切换（对齐桌面端 PriceMode_Changed：仅当前价未被手改时切换默认价）----
function onPriceTypeChange() {
  if (!dlgState.part) return;
  const type = document.querySelector('input[name="priceType"]:checked').value;
  const current = parseFloat($('dlgPrice').value) || 0;
  const part = dlgState.part;
  if (current === part.oprc || current === part.oprb || current === 0)
    $('dlgPrice').value = type === 'retail' ? (part.oprc ?? '') : (part.oprb ?? '');
  updateDlgAmount();
}

// ---- 金额实时计算 + 数量/负库存拦截（对齐桌面端 UpdateAmount：库存 0 / 数量超库存禁用确认 + 红字提示）----
// 校验顺序：数量非法（非整数/非正）优先 → 库存 0 → 超库存，同一时刻只显示一条，避免矛盾提示
function updateDlgAmount() {
  if (!dlgState.part) return;
  const p = dlgState.part;
  const rawQty = String($('dlgAmount').value ?? '').trim();
  const qty = Number(rawQty);
  const price = parseFloat($('dlgPrice').value) || 0;
  $('dlgAmountCalc').textContent = '¥' + fmt(Number.isFinite(qty) ? qty * price : 0);

  const warn = $('dlgStockWarn');
  const btn = $('btnAddDetail');
  const stock = Number(p.kcamount ?? 0);
  if (!isQtyInteger(rawQty) || qty <= 0) {
    warn.textContent = '⚠ 数量必须为整数（如 2），且大于 0';
    warn.style.display = '';
    btn.disabled = true;
  } else if (stock <= 0) {
    warn.textContent = '⚠ 该配件实际库存为 0，不允许销售（负库存拦截）';
    warn.style.display = '';
    btn.disabled = true;
  } else if (qty > stock) {
    warn.textContent = `⚠ 销售数量 ${qty} 超过实际库存 ${stock}，不允许负库存出库`;
    warn.style.display = '';
    btn.disabled = true;
  } else {
    warn.style.display = 'none';
    btn.disabled = false;
  }
}

// ---- 数量输入守卫（2026-11-07 口径：全部整数卖出，小数整段拒绝且保持原值）----
// 输入阶段：按下小数点/逗号 → 本次输入整体作废，数量框回退到「进入编辑前的原值」（弹窗打开时那个值）；
// 粘贴阶段：含小数或非数字的内容整段拒绝，同样回退到原值（绝不静默按 2 处理）；
// 兜底：拖拽/输入法等绕过键盘事件的非法内容，同样回退到原值。
// 负号/加号/科学计数：仅拦住该字符、不落框，值不受影响。
function revertQtyInput(msg) {
  const el = $('dlgAmount');
  const origin = dlgState.origQty || '1';
  el.value = origin;
  el.focus();
  el.select();          // 全选原值，便于直接重新输入整数
  showToast(msg, 'warn');
  updateDlgAmount();
}

function bindQtyInputGuards() {
  const el = $('dlgAmount');
  if (!el) return;

  el.addEventListener('keydown', e => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;      // 放行 Ctrl+V / Ctrl+A 等快捷键
    if (['.', ','].includes(e.key)) {
      e.preventDefault();
      revertQtyInput(`数量必须为整数，本次输入已作废，已恢复为 ${dlgState.origQty || '1'}`);
      return;
    }
    if (['-', '+', 'e', 'E', ' '].includes(e.key)) {
      e.preventDefault();
      showToast('数量只能输入整数，不支持负号/科学计数', 'warn');
    }
  });

  el.addEventListener('paste', e => {
    const txt = String((e.clipboardData || window.clipboardData)?.getData('text') || '').trim();
    e.preventDefault();
    if (!txt) return;
    if (!/^[0-9]+$/.test(txt)) {
      revertQtyInput(`粘贴内容「${txt}」不是整数，本次输入已作废，数量已恢复为 ${dlgState.origQty || '1'}`);
      return;
    }
    const start = el.selectionStart ?? el.value.length;
    const end = el.selectionEnd ?? el.value.length;
    el.value = (el.value.slice(0, start) + txt + el.value.slice(end)).replace(/^0+(?=\d)/, '');
    el.setSelectionRange(el.value.length, el.value.length);
    updateDlgAmount();
  });

  el.addEventListener('input', () => {
    const v = el.value;
    if (/^(|[0-9]+)$/.test(v)) { updateDlgAmount(); return; }
    revertQtyInput(`数量只能输入整数，本次输入已作废，已恢复为 ${dlgState.origQty || '1'}`);
  });
}

// 确认加入明细（对齐桌面端 BtnOk_Click：负库存双保险 + 最近一次进价检测）
function addDetailFromDialog() {
  const p = dlgState.part;
  if (!p) return;

  const rawQty = String($('dlgAmount').value ?? '').trim();
  if (!isQtyInteger(rawQty)) {
    showToast('数量必须为整数（如 2），不能含小数点或非数字', 'error');
    $('dlgAmount').focus(); $('dlgAmount').select();
    return;
  }
  const qty = Number(rawQty);
  if (qty <= 0) { showToast('数量必须大于 0', 'error'); $('dlgAmount').focus(); return; }
  if (qty > QTY_MAX) { showToast(`数量不能超过 ${QTY_MAX}`, 'error'); $('dlgAmount').focus(); return; }
  // 双保险：回车从 dlgPrice 直接调本方法会绕过按钮 disabled，负库存此处按桌面端 BtnOk_Click 同口径再拦一道
  const stock = Number(p.kcamount ?? 0);
  if (stock <= 0) {
    showToast('该配件实际库存为 0，不允许销售（负库存拦截）', 'error');
    return;
  }
  if (qty > stock) {
    showToast(`销售数量 ${qty} 超过实际库存 ${stock}，不允许负库存出库`, 'error');
    return;
  }

  const price = parseFloat($('dlgPrice').value) || 0;
  if (price < 0) { showToast('单价不能为负', 'error'); $('dlgPrice').focus(); return; }

  // 最近一次进价检测（2026-11-04 需求）：售价低于最近进价 → 弹窗确认后才写入
  const lastIprj = p.lastIprj ?? 0;
  if (lastIprj > 0 && price < lastIprj) {
    if (!confirm(`销售单价 ${fmt(price)} 低于最近一次进价 ${fmt(lastIprj)}，确定按 ${fmt(price)} 写入吗？`)) return;
  }

  const data = {
    kcid: p.id,
    cp: p.cp,
    nno: p.nno,
    na1: p.na1,
    qty: qty,
    price: price,
    subTotal: Math.round(price * qty * 100) / 100,
    ty: $('dlgCartype').value.trim(),
    remark: $('dlgMemo').value.trim(),
  };

  if (dlgState.editIndex >= 0 && state.details[dlgState.editIndex]) {
    state.details[dlgState.editIndex] = data;
    showToast(`已修改: ${p.na1} x${qty}`);
  } else {
    state.details.push(data);
    showToast(`已添加: ${p.na1} x${qty}`);
  }
  closePartDialog();
  renderDetails();
  calcTotals();
}

// ========== 四、销售明细渲染 ==========
function renderDetails() {
  const tbody = $('detailBody');
  if (state.details.length === 0) {
    tbody.innerHTML = '<tr><td colspan="9" class="empty-hint">点击上方配件列表添加明细</td></tr>';
    $('detailCount').textContent = '0';
    return;
  }

  tbody.innerHTML = state.details.map((d, i) => `
    <tr>
      <td>${escHtml(d.cp)}</td>
      <td>${escHtml(d.nno)}</td>
      <td>${escHtml(d.na1)}</td>
      <td>${escHtml(d.ty)}</td>
      <td style="text-align:center">${fmtQty(d.qty)}</td>
      <td style="text-align:right">${fmt(d.price)}</td>
      <td style="text-align:right;font-weight:500">${fmt(d.subTotal)}</td>
      <td>${escHtml(d.remark)}</td>
      <td><button class="btn btn-sm btn-danger" onclick="removeDetail(${i})">删</button></td>
    </tr>`).join('');
  $('detailCount').textContent = state.details.length;
}

function removeDetail(index) {
  state.details.splice(index, 1);
  renderDetails();
  calcTotals();
}

// ========== 五、金额计算 ==========
function calcTotals() {
  let total = 0, sumQty = 0;
  state.details.forEach(d => { total += d.price * d.qty; sumQty += d.qty; });
  total = Math.round(total * 100) / 100;

  $('totalAmount').value = '¥' + fmt(total);
  $('sumLabel').textContent = `合计: ¥${fmt(total)} | 数量: ${fmtQty(sumQty)}`;
}

// ========== 六、确认开单（POST /api/sales） ==========
async function submitOrder() {
  if (state.details.length === 0) { showToast('请至少添加一条明细', 'error'); return; }
  if (!state.me?.deptNo) { showToast('当前账号无部门信息，无法开单', 'error'); return; }
  // 提交前再校验一次数量（2026-11-07 口径：全部整数卖出，非整数直接拦截，不做静默截断）
  const badLine = state.details.find(d => !isQtyInteger(d.qty) || Number(d.qty) <= 0);
  if (badLine) {
    showToast(`明细「${badLine.na1}」数量必须为整数且大于 0，请修改后再提交`, 'error');
    return;
  }

  const command = {
    Dept: state.me.deptNo,       // 服务端会以 JWT 强制覆盖，仅作占位
    Gno: state.selectedClient?.gno || null,
    Gname: state.selectedClient?.gname || null,
    Remark: $('memoText').value.trim() || null,
    DocType: '销售',
    Lines: state.details.map(d => ({
      Cp: d.cp,
      Nno: d.nno,
      Qty: d.qty,
      Price: d.price || null,
      Ty: d.ty || null,
      Remark: d.remark || null,
      Kcid: d.kcid,
    })),
  };

  try {
    const btn = $('btnSubmit');
    btn.disabled = true; btn.textContent = '提交中...';
    showToast('正在提交...');
    const res = await authFetch(API('/sales'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(command),
    });
    const result = await readApi(res);
    // 桌面端同构：开单保存后弹「整单确认」（付款方式/优惠等），确认成功才清空面板
    openConfirmDialog(result);
  } catch (e) {
    showToast('开单失败: ' + e.message, 'error');
  } finally {
    const btn = $('btnSubmit');
    btn.disabled = false; btn.textContent = '确认开单';
  }
}

function clearAll() {
  state.details = [];
  state.selectedClient = null;
  $('clientInput').value = '';
  $('memoText').value = '';
  $('qPartNo').value = ''; $('qPartName').value = ''; $('qCartype').value = '';
  $('partsBody').innerHTML = '<tr><td colspan="9" class="empty-hint">输入条件后回车或等待自动搜索...</td></tr>';
  renderDetails();
  calcTotals();
}

// ========== 七、整单确认（复刻桌面端 ConfirmSaleDialog：row298 写 guno 等单头字段，状态由「开单中」转出） ==========
let confirmState = { paper: '', gno: '', gname: '', amountBefore: 0 };

async function openConfirmDialog(createResult) {
  confirmState = {
    paper: createResult.paper,
    gno: state.selectedClient?.gno || '',
    gname: state.selectedClient?.gname || '',
    amountBefore: createResult.totalAmount,
  };
  $('cfPaper').textContent = confirmState.paper;
  $('cfClient').textContent = confirmState.gname || '散客';
  $('cfAmountBefore').textContent = '¥' + fmt(confirmState.amountBefore);
  $('cfOutdate').value = new Date().toISOString().slice(0, 10);
  $('cfYhl').value = '0';
  $('cfYhlType').value = '1';
  $('cfRemark').value = $('memoText').value.trim();
  $('cfFphs').value = '';
  resetCredit();

  $('confirmModal').classList.add('show');

  // 字典加载（付款方式/发货方式/销售类型，会话级缓存）
  try {
    if (!state.confirmOptions) {
      const res = await authFetch(API('/sales/confirm-options'));
      state.confirmOptions = await readApi(res);
    }
    const opts = state.confirmOptions;
    fillSelect('cfGuno', opts.paymentMethods, '现金');
    fillSelect('cfFhfs', opts.fhfs, '送货');
    fillSelect('cfXslx', opts.paperTypes, '仓库');
  } catch (e) {
    showToast('确认字典加载失败: ' + e.message, 'error');
  }

  recalcConfirm();
  loadCustomerCredit();
}

function fillSelect(id, items, preferred) {
  const sel = $(id);
  sel.innerHTML = (items || []).map(v => `<option value="${escAttr(v)}">${escHtml(v)}</option>`).join('');
  if (items?.includes(preferred)) sel.value = preferred;
  else if (items?.length) sel.selectedIndex = 0;
}

// 客户信用参考（限额/预警限额/余额）+ 客户档案发票号带入（桌面端同源）
// 版式改动后拆成三个独立数值节点（cfXede/cfYjxe/cfYue），读取与回填同步
function resetCredit() {
  $('cfXede').textContent = '0.00';
  $('cfYjxe').textContent = '0.00';
  $('cfYue').textContent = '0.00';
  $('cfYue').classList.remove('cf-credit-neg');
  const hint = $('cfCreditHint');
  hint.textContent = '';
  hint.style.display = 'none';
}

async function loadCustomerCredit() {
  const gname = confirmState.gname;
  const hint = $('cfCreditHint');
  if (!gname) {
    resetCredit();
    hint.textContent = '散客：无客户信用信息';
    hint.style.display = '';
    return;
  }
  try {
    const res = await authFetch(API(`/sales/customer-credit?gname=${encodeURIComponent(gname)}`));
    const c = await readApi(res);
    $('cfXede').textContent = fmt(c.xede);
    $('cfYjxe').textContent = fmt(c.yjxe);
    $('cfYue').textContent = fmt(c.yue);
    const neg = (Number(c.yue) || 0) < 0;   // 余额为负 = 客户欠款，标红
    $('cfYue').classList.toggle('cf-credit-neg', neg);
    hint.style.display = 'none';
    if (c.pno) $('cfFphs').value = c.pno;
  } catch { resetCredit(); }
}

// 优惠后应收试算（对齐桌面端 RecalcAfter CBOTYPE 三方式）
function recalcConfirm() {
  const yhl = parseFloat($('cfYhl').value) || 0;
  const mode = $('cfYhlType').value;
  const before = confirmState.amountBefore;
  const after = mode === '2' ? before - yhl
    : mode === '3' ? before * (100 + yhl) / 100
    : before * (100 - yhl) / 100;
  $('cfAmountAfter').textContent = '¥' + fmt(after);
}

async function confirmDoc() {
  const guno = $('cfGuno').value;
  if (!guno) { showToast('请选择支付方式', 'error'); return; }

  const btn = $('btnConfirmDoc');
  btn.disabled = true;
  try {
    const command = {
      Dept: state.me?.deptNo || '',        // 服务端以 JWT 强制覆盖（required 字段反序列化需占位）
      Paper: confirmState.paper,
      DocType: '销售',
      Gno: confirmState.gno || '',
      Gname: confirmState.gname || '',
      Guno: guno,
      Pno: '',
      TMode: $('cfFhfs').value || '',
      Yhl: parseFloat($('cfYhl').value) || 0,
      YhlMode: parseInt($('cfYhlType').value, 10) || 1,
      Outdate: $('cfOutdate').value || new Date().toISOString().slice(0, 10),
      Remark: $('cfRemark').value.trim(),
      Fphs: $('cfFphs').value.trim(),
      Wlgs: '',
      Papertype: $('cfXslx').value || '',
      AmountBefore: confirmState.amountBefore,
      AmountAfter: parseFloat(($('cfAmountAfter').textContent || '').replace('¥', '')) || 0,
    };
    const res = await authFetch(API(`/sales/${encodeURIComponent(confirmState.paper)}/confirm`), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(command),
    });
    const result = await readApi(res);
    $('confirmModal').classList.remove('show');
    showToast(`已确认 ${result.paper}（结帐日期 ${formatDate(result.jzdate)}）`, 'success');
    if (window.confirm(`已确认单据 ${result.paper}\n是否立即打印（默认打印机）？`)) {
      await desktopPrint(result.paper, '销售');
    }
    clearAll();
  } catch (e) {
    showToast('确认失败: ' + e.message, 'error');
  } finally {
    btn.disabled = false;
  }
}

// cancelled=true：用户取消确认（与桌面端一致：面板保留，单据停在「开单中」）
function closeConfirmDialog(cancelled) {
  $('confirmModal').classList.remove('show');
  if (cancelled) showToast(`单据 ${confirmState.paper} 已保存但未确认（状态：开单中），可重新开单确认`, 'warn');
}

// ========== 八、单据查询（GET /api/docs） ==========
async function queryBills() {
  const paper = $('qPaper').value.trim();
  const start = $('qStart').value;
  const end = $('qEnd').value;
  const gname = $('qClient').value.trim();
  const per = $('qPer').value;

  const params = new URLSearchParams({
    page: String(state.queryPage), pageSize: '20',
    ...(paper && { paper }),
    ...(start && { dateFrom: start }),
    ...(end && { dateTo: end }),
    ...(gname && { gname }),
    ...(per && { per }),
  });

  try {
    const res = await authFetch(API(`/docs?${params}`));
    const data = await readApi(res);
    renderBills(data.items || [], data.page, data.pageSize, data.total);
  } catch (e) {
    showToast('查询失败: ' + e.message, 'error');
  }
}

// sk 由后端派生：'已确认'/'已提货'/'已入库'/'开单中'/''（DocQueryRepository L201-205）
const SK_OK = ['已确认', '已提货', '已入库'];
function skBadgeHtml(sk) {
  const s = (sk || '').trim();
  if (!s) return '<span class="flag-badge flag-3">—</span>';
  return `<span class="flag-badge ${SK_OK.includes(s) ? 'flag-1' : 'flag-0'}">${escHtml(s)}</span>`;
}

function renderBills(bills, page, pageSize, total) {
  const tbody = $('billBody');
  if (!bills || bills.length === 0) {
    tbody.innerHTML = '<tr><td colspan="9" class="empty-hint">没有找到单据</td></tr>';
    $('pagination').innerHTML = '';
    return;
  }

  tbody.innerHTML = bills.map(b => {
    return `<tr onclick="showBillDetail('${escAttr(b.paper)}', '${escAttr(b.dpm)}', this)"
      data-paper="${escAttr(b.paper)}" data-date="${escAttr(b.outdate)}" data-gname="${escAttr(b.gname)}"
      data-per="${escAttr(b.per)}" data-remark="${escAttr(b.remark)}"
      data-linecount="${b.lineCount ?? ''}" data-totalqty="${b.totalQty ?? ''}"
      data-totalamount="${b.totalAmount ?? ''}" data-totaloprct="${b.totalOprct ?? ''}"
      data-gun="${escAttr(b.guno)}" data-sk="${escAttr(b.sk)}">
      <td>${escHtml(b.paper)}</td>
      <td>${formatDate(b.outdate)}</td>
      <td>${escHtml(b.gname)}</td>
      <td>${escHtml(b.per)}</td>
      <td style="text-align:center">${fmtQty(b.lineCount)}</td>
      <td style="text-align:right">${fmtQty(b.totalQty)}</td>
      <td style="text-align:right;color:#1565c0;font-weight:600">${fmt(b.totalAmount)}</td>
      <td style="text-align:right">${fmt(b.totalOprct)}</td>
      <td>${skBadgeHtml(b.sk)}</td>
    </tr>`;
  }).join('');

  const totalPages = Math.ceil((total || bills.length) / (pageSize || 20));
  renderPagination(page || 1, totalPages);
}

function renderPagination(current, total) {
  if (total <= 1) { $('pagination').innerHTML = `<span class="page-info">共 ${current}/${total} 页</span>`; return; }
  let html = '';
  html += `<button onclick="goPage(${current - 1})" ${current <= 1 ? 'disabled' : ''}>上一页</button>`;
  for (let i = Math.max(1, current - 2); i <= Math.min(total, current + 2); i++) {
    html += `<button onclick="goPage(${i})" ${i === current ? 'disabled' : ''}>${i}</button>`;
  }
  html += `<button onclick="goPage(${current + 1})" ${current >= total ? 'disabled' : ''}>下一页</button>`;
  html += `<span class="page-info">第 ${current}/${total} 页</span>`;
  $('pagination').innerHTML = html;
}

async function goPage(page) {
  if (page < 1) return;
  state.queryPage = page;
  await queryBills();
}

// 单据详情（先显示摘要，再异步加载明细）
async function showBillDetail(paper, dpm, rowEl) {
  state.lastDetailPaper = paper;
  state.lastDetailDpm = dpm;
  // 立即用列表行数据显示摘要（零网络延迟）
  if (rowEl && dpm) {
    renderBillDetailHead({
      paper: rowEl.dataset.paper, outdate: rowEl.dataset.date,
      gname: rowEl.dataset.gname, per: rowEl.dataset.per,
      lineCount: rowEl.dataset.linecount, totalQty: rowEl.dataset.totalqty,
      totalAmount: rowEl.dataset.totalamount, totalOprct: rowEl.dataset.totaloprct,
      guno: rowEl.dataset.guno, sk: rowEl.dataset.sk, remark: rowEl.dataset.remark,
    });
    $('billDetailBody').innerHTML += '<div id="detailLoading" style="text-align:center;padding:20px;color:#999">加载明细中...</div>';
  }
  $('billDetailModal').classList.add('show');

  try {
    const res = await authFetch(API(`/docs/${encodeURIComponent(paper)}?dpm=${encodeURIComponent(dpm)}`));
    const data = await readApi(res);
    renderBillDetail(data);
  } catch (e) {
    const loadingEl = document.getElementById('detailLoading');
    if (loadingEl) loadingEl.textContent = '加载失败：' + e.message;
    else showToast('获取详情失败: ' + e.message, 'error');
  }
}

function renderBillDetailHead(b) {
  $('billDetailBody').innerHTML = `
    <div class="bill-detail-head">
      <bdp><label>单号</label><value>${escHtml(b.paper)}</value></bdp>
      <bdp><label>日期</label><value>${formatDate(b.outdate)}</value></bdp>
      <bdp><label>客户</label><value>${escHtml(b.gname) || '散客'}</value></bdp>
      <bdp><label>操作员</label><value>${escHtml(b.per)}</value></bdp>
      <bdp><label>付款方式</label><value>${escHtml(b.guno) || '-'}</value></bdp>
      <bdp><label>状态</label><value>${skBadgeHtml(b.sk)}</value></bdp>
      <bdp><label>数量</label><value>${fmtQty(b.totalQty)}</value></bdp>
      <bdp><label>折后总额</label><value>¥${fmt(b.totalAmount)}</value></bdp>
      <bdp><label>实收</label><value>¥${fmt(b.totalOprct)}</value></bdp>
      <bdp><label>备注</label><value>${escHtml(b.remark) || '-'}</value></bdp>
    </div>`;
}

function closeBillDetail() {
  $('billDetailModal').classList.remove('show');
}

function renderBillDetail(data) {
  const s = data.summary || {};
  const lines = data.lines || [];
  // OutDocSummary.mark 为原始标志：'Y'已确认 / 'T'已提货 / 'R'已入库 / 其他=开单中
  const skText = { Y: '已确认', T: '已提货', R: '已入库' }[(s.mark || '').trim()] || '开单中';

  let html = `
    <div class="bill-detail-head">
      <bdp><label>单号</label><value>${escHtml(s.paper)}</value></bdp>
      <bdp><label>日期</label><value>${formatDate(s.outdate)}</value></bdp>
      <bdp><label>客户</label><value>${escHtml(s.gname) || '散客'}</value></bdp>
      <bdp><label>客户编号</label><value>${escHtml(s.gno) || '-'}</value></bdp>
      <bdp><label>操作员</label><value>${escHtml(s.per)}</value></bdp>
      <bdp><label>付款方式</label><value>${escHtml(s.guno) || '-'}</value></bdp>
      <bdp><label>状态</label><value><span class="flag-badge ${(s.mark || '').trim() ? 'flag-1' : 'flag-0'}">${skText}</span></value></bdp>
      <bdp><label>实收合计</label><value>¥${fmt(s.totalOprct)}</value></bdp>
      <bdp><label>折后合计</label><value>¥${fmt(s.totalDiscounted)}</value></bdp>
      <bdp><label>备注</label><value>${escHtml(s.remark) || '-'}</value></bdp>
    </div>
    <div class="table-wrap">
      <table class="data-table">
        <thead><tr>
          <th>编号</th><th>内部编号</th><th>名称</th><th>数量</th>
          <th>单价</th><th>小计</th><th>车型</th><th>货位</th><th>备注</th>
        </tr></thead>
        <tbody>`;

  if (lines.length === 0) {
    html += '<tr><td colspan="9" class="empty-hint">无明细</td></tr>';
  } else {
    lines.forEach(d => {
      html += `<tr>
        <td>${escHtml(d.cp)}</td>
        <td>${escHtml(d.nno)}</td>
        <td>${escHtml(d.na1)}</td>
        <td style="text-align:center">${fmtQty(d.ckamount)}</td>
        <td style="text-align:right">${fmt(d.oprc)}</td>
        <td style="text-align:right;font-weight:500">${fmt(d.oprct)}</td>
        <td>${escHtml(d.ty)}</td>
        <td>${escHtml(d.posi)}</td>
        <td>${escHtml(d.remark)}</td>
      </tr>`;
    });
  }
  html += '</tbody></table></div>';

  html += `<div style="margin-top:12px;text-align:right">
    <button class="btn btn-sm btn-primary" onclick="printBill(state.lastDetailPaper)">打印单据</button>
  </div>`;

  $('billDetailBody').innerHTML = html;
}

function printBill(paper, type = '销售') {
  if (!paper) { showToast('未选择单据', 'error'); return; }
  desktopPrint(paper, type);
}

// 单据打印：调用桌面端静默打印（票面/取数/打印机全部复用桌面端，默认打印机，不弹浏览器打印）
async function desktopPrint(paper, type = '销售') {
  showToast('正在发送到打印机...');
  try {
    const res = await authFetch(API(`/print/${encodeURIComponent(paper)}/desktop?type=${encodeURIComponent(type)}`), {
      method: 'POST',
    });
    const data = await readApi(res, '打印失败');
    showToast(`已发送到打印机：${data.printer || '默认打印机'}`, 'success');
    return true;
  } catch (e) {
    // 网络层失败（Web 服务/桌面端未响应）给出明确提示，不静默失败
    const reason = (e && (e.name === 'TypeError' || /Failed to fetch|NetworkError|Load failed/i.test(e.message || '')))
      ? '无法连接桌面打印服务（Web 服务未响应）'
      : ((e && e.message) || '未知错误');
    showToast('打印失败：' + reason, 'error');
    return false;
  }
}

// ========== 启动 ==========
init();
