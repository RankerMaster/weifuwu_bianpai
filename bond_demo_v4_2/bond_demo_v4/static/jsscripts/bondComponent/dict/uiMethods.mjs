// import lf & plugins
//import LogicFlow from './@logicflow/core'
//import './@logicflow/core/dist/style/index.css'
//import { Menu, Group, DndPanel, Snapshot, SelectionSelect } from './@logicflow/extension'

// import commonConfig
import { editConfig, keyboardConfig, gridConfig, extraConfig } from './common/commonConfig.mjs'

import { translateComponentConfig } from './componentConfigFunction.mjs'


async function getBondConfig(){
  let {data: baseNodeConfig} = await axios.get("/static/jsconfig/bond-config.json");
  return baseNodeConfig;
}

const baseNodeConfig = await getBondConfig();

var registerNodeList = [];
var nodePanelList = {};

Object.entries(baseNodeConfig).forEach(paramItem => {
  let registerNodeList_ = paramItem[1].config.map(paramItem_ => translateComponentConfig(
    {
      type: paramItem_.type,
      text: paramItem_.name,
      icon: '/static/images/' + paramItem_.icon + '.png',
      typeColor: paramItem_.typeColor || "#ffffff",
      inputType: paramItem_.inputType,
      outputType: paramItem_.outputType,
      inCount: paramItem_.inCount,
      outCount: paramItem_.outCount,
      componentName: paramItem_.componentName,
      params: paramItem_.params || [],
    }));
  registerNodeList = registerNodeList.concat(registerNodeList_);

  nodePanelList[paramItem[0]] = {
    name: paramItem[1].name,
    config: paramItem[1].config.map(paramItem_ => {
    return {
      type: paramItem_.type,
      text: paramItem_.name,
      inputType: paramItem_.inputType,
      outputType: paramItem_.outputType,
      inCount: paramItem_.inCount,
      outCount: paramItem_.outCount,
      properties: {
        icon: '/static/images/' + paramItem_.icon + '.png',
        typeColor: paramItem_.typeColor || "#ffffff",
        componentName: paramItem_.componentName,
        params: paramItem_.params || [],
      }
    }})
}});

// import group
//const groupModulesFiles = require.context('./group', true, /.js$/)
let registerGroupList = []
/**groupModulesFiles.keys().forEach((modulePath) => {
  registerGroupList.push(groupModulesFiles(modulePath))
})**/
import SubSystemConfig from './group/SubSystem.mjs'
registerGroupList.push(SubSystemConfig)

// import edge
//const edgeModulesFiles = require.context('./edge', true, /.js$/)
let registerEdgeList = []
/**edgeModulesFiles.keys().forEach((modulePath) => {
  registerEdgeList.push(edgeModulesFiles(modulePath))
})**/
import CustomEdgeConfig from './edge/CustomEdge.mjs'
registerEdgeList.push(CustomEdgeConfig)

import { utils } from './tools/util.mjs'
import { listeners } from './tools/listeners.mjs'
import { menu } from './tools/menu.mjs'

const uiMethods = {
  // 初始化 LogicFlow
  $_initLf () {
    // 画布配置
    const lf = new LogicFlow({
      container: this.$refs.container,
      // 页面编辑状态选项
      ...editConfig,
      // 自定义键盘快捷键
      keyboard: keyboardConfig,
      // 网格
      grid: gridConfig.enabled ? gridConfig : false,
      // 插件
      plugins: [
        Menu,
        Group,
        Snapshot,
        DndPanel,
        SelectionSelect
      ]
    })
    this.lf = lf
    this.lf.extension.selectionSelect.setSelectionSense(extraConfig.SelectionSense.isWholeEdge, extraConfig.SelectionSense.isWholeNode)
    this.$_registerNode()
  },
  // 注册节点
  $_registerNode () {
    // node register
  registerNodeList.forEach((node) => {
      this.lf.register(node)
    })
    // group register
    registerGroupList.forEach((group) => {
      this.lf.register(group)
    })
    // edge register
    registerEdgeList.forEach((edge) => {
      this.lf.register(edge)
    })

    this.lf.setDefaultEdgeType(extraConfig.defaultEdgeType)
    this.$_render()
  },
  ...menu,
  ...listeners,
  ...utils
}

/**
 * 子系统初始化
 */
const subsystemInit = {
  type: "sub-system",
  text: '子系统',
  properties: {
  }
}

export { uiMethods, nodePanelList, subsystemInit }