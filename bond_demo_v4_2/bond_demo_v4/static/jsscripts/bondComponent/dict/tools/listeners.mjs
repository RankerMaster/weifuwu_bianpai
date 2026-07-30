/**
 * 添加事件监听参考 http://logic-flow.org/guide/basic/event.html
 * LogicFlow 提供的事件参考 http://logic-flow.org/api/eventCenterApi.html
 * 也可以监听基于 LogicFlow eventCenter 抛出的自定义事件，如何抛出自定义事件参考 http://logic-flow.org/api/graphModelApi.html#eventcenter
 */
const listeners = {
  $_LfEvent () {
    // 单击节点 测试用
    this.lf.on('node:click', ({ data }) => {
      if (data.properties.scopedata) {
        this.scopeVisible = true;
        this.scopeItemData = {
          data: data.properties.scopedata,
          timeData: this.lf.timeData,
        };
        this.$forceUpdate();
      }
    })
    // 双击节点
    this.lf.on('node:dbclick', ({ data }) => {
      this.formData = data;
      this.dialogVisible = true;
    })
    // ※节点信息编辑
    this.lf.on('node:edit', (data) => {
      this.formData = data
      this.dialogVisible = true
    })
    // 鼠标进入节点
    this.lf.on('node:mouseenter', ({ data }) => {
    })
    // 鼠标离开节点
    this.lf.on('node:mouseleave', (node) => {
    })
    // 连线删除
    this.lf.on('edge:delete', ({data}) => {
    })
    // 锚点连线拖动连线成功时触发，主要用于添加额外的连线验证
    this.lf.on('anchor:drop', (data) => {
      //console.log(data.edgeModel)
    })
    // ※子系统折叠 & 展开
    this.lf.on('group:fold', (data) => {
      if (data.isFolded === true) {
        data.foldGroup(false)
        this.foldAllChild(data.children)
        data.foldGroup(true)
      } else {
        this.unfoldAllChild(data.children)
      }
    })
  }
}

export { listeners }