import { uiMethods, nodePanelList, subsystemInit } from './dict/uiMethods.mjs'
import { importStruct, exportStruct } from './dict/common/methods.mjs'

function setDefault(val, val_default){
	if (val === null || val === undefined){
		return val_default;
	}else{
		return val;
	}
};

let nodePanel = Vue.component(
	'ate-bond-node-panel',
	{
		props: ["lf"],
		template: `
			<div class="node-panel">
				<el-select v-model="nodeField">
					<el-option :label="itm.name" :value="itmKey" :key="itmKey" v-for="(itm, itmKey) in nodeList"></el-option>
				</el-select>
				<div v-if="nodeField" 
					@mousedown.prevent="e=>e.preventDefault()"
					@mousemove.prevent="e=>e.preventDefault()">
					<div class="red-ui-palette-node ui-draggable ui-draggable-handle"
						@mousemove.prevent="e=>e.preventDefault()"
						@mousedown.stop="$_dragNode(itm)" v-for="(itm, index) of nodeList[nodeField].config" :key="index"
						:style="{ backgroundColor: itm.properties.typeColor }">
						<div class="red-ui-palette-label">
							<span @mousemove.prevent="e=>e.preventDefault()"
								@mousedown.prevent="e=>e.preventDefault()"
								v-if="itm.text.length<=6">{{ itm.text }}</span>
							<el-tooltip class="item" effect="dark" :content="itm.text" placement="top-start" v-else
								@click.native.prevent="e=>e.preventDefault()">
								<span @mousemove.prevent="e=>e.preventDefault()"
									@mousedown.prevent="e=>e.preventDefault()">{{itm.text.slice(0,5)}}...</span>
							</el-tooltip>
						</div>
						<div class="red-ui-palette-icon-container">
							<div class="red-ui-palette-icon"
								:style="{ backgroundImage: 'url(' + itm.properties.icon + ')'}"></div>
						</div>
					</div>
				</div>
			</div>
		`,
		data() {
			return {
				nodeList: nodePanelList,
				nodeField: null,
			}
		},
		mounted(){
			document.onselectstart = function() { return false; };
		},
		methods: {
			setDefault,
			$_dragNode (item) {
				this.$props.lf.dnd.startDrag({
					type: item.type,
					text: item.text,
					properties: item.properties,
				});
			},
		}
	});

let editDialog = Vue.component(
	'ate-bond-edit-dialog',
	{
		props: ["dialogVisible", "form-data", "scope-mode", "scope-data"],
		template: `
		<div>
			<el-dialog title="编辑元件属性" width="60dvw" :visible.sync="dialogVisible"
					:before-close="closeDialog" @open="openDialog" style="padding: 10px;">

				<el-form :model="form" :rules="rules" ref="NodePanelDialogForm" v-if="form" :style="{height: Math.min(50, setDefault(form.properties.params, []).length*10+5)+'dvh', 'overflow-y': 'auto'}">
					<el-form-item label="名称" label-width='120px'>
						<el-input v-model="form.text.value"></el-input>
					</el-form-item>
					<el-form-item :label="itm.name" label-width='120px' v-for="(itm, itmId) in form.properties.params" :key="itmId">
						<el-input :value="itm.value" @input="val=>changeValue(itmId, val, false)" v-if="itm.isGlobal">
							<template slot="append">全局变量</template>
						</el-input>
						<el-input :value="itm.value" @input="val=>changeValue(itmId, val)" v-else-if="!itm.isFunc & itm.key.slice(0,7) == 'source_'"></el-input>
						<el-input type="number" :step="0.00001" :value="itm.value" @input="val=>changeValue(itmId, val)" v-else-if="!itm.isFunc"></el-input>
						<el-upload v-else class="upload-demo" size="mini" 
							action="" :auto-upload="false" accept=".csv" :multiple="false" :show-file-list="false"
							:on-change="(val, _)=>changeValue(itmId, val, true)">
							<el-button size="mini" type="primary">点击上传</el-button>
							<div slot="tip" class="el-upload__tip">
								<el-tag size="mini" type="success" v-if="itm.value" @click="clickParam=itm.name; clickValue=itm.value; clickDialogShow=true;">已上传文件</el-tag>
								<el-tag size="mini" type="info" v-else>请上传csv/xs/xlsx文件</el-tag>
							</div>
						</el-upload>
					</el-form-item>
				</el-form>

				<div ref="dialogscopeitemarea" style="width: 90%; height: 45dvh; overflow-y: auto;" v-if="Object.keys(setDefault(scopeData, {})).length > 0"></div>

				<span slot="footer" class="dialog-footer" v-if="form">
					<el-button @click="closeDialog">取 消</el-button>
					<el-button type="primary" @click="formDataUpdate">确 定</el-button>
				</span>
			</el-dialog>
			<el-dialog :title="clickParam" width="50dvw" :visible.sync="clickDialogShow" :before-close="closeClickDialog" 
						@open="openClickDialog">
				<div ref="dialogparamitemarea" style="width: 90%; height: 40dvh; overflow-y: auto;"></div>
			</el-dialog>
		</div>
		`,
		data() {
			return {
				form: {
					id: "elpsycongoroo",
					text: {
						value: ''
					},
					properties: {
						params: [],
					},
				},
				rules: {
					'text.value': [{ required: true, message: '请输入元件名称', trigger: 'blur' }],
				},
				clickParam: "",
				clickValue: null,
				itemZone: null,
				itemClickZone: null,
				clickDialogShow: false,
			}
		},
		methods: {
			setDefault,
			changeValue (itmId, val, isFunc) {
				//console.log(val)
				if (isFunc){
					let loading = this.$loading({
						lock: true,
						text: '解析中，请稍候...',
						spinner: 'el-icon-loading',
						background: 'rgba(0, 10, 0, 0.5)'
					})
					let fd = new FormData();
					fd.append('csvFile', val.raw);
					axios.post('/bond-analyse/convert-csv-file/', fd)
					.then((response) => {
						this.$set(this.form.properties.params[itmId], "value", response.data);
						loading.close();
					}).catch(e=>{
						loading.close();
					});
				}else{
					this.$set(this.form.properties.params[itmId], "value", val);
				}
				this.$set(this.form.properties.params[itmId], "changed", true);
				this.$forceUpdate();
				//console.log(this.form.properties.params[itmId])
			},
			initItemZone (data, timeData) {
				let itemData = data.map(itm =>{
					return {
							name: itm.label,
							type: 'line',
							data: itm.data.map((itm, itmId)=>{return [timeData[itmId], itm]}),
						}
					})
				this.itemZone = echarts.init(this.$refs.dialogscopeitemarea);
				this.itemZone.setOption({
					tooltip: {
						trigger: 'axis'
					},
					grid: {
						left: '15%',
						right: '15%',
						bottom: '10%'
					},
					xAxis: {
						//data: timeData,
						type: 'value',
					},
					yAxis: {
						min: function(value){
							return value.min - Math.max(1E-9, 0.2*(value.max - value.min));
						},
						max: function(value){
							return value.max + Math.max(1E-9, 0.2*(value.max - value.min));
						},
						type: 'value',
					},
					toolbox: {
						right: 10,
						feature: {
							//dataZoom: {
								//yAxisIndex: 'none'
							//},
							restore: {},
							saveAsImage: {}
						}
					},
					legend: {
						orient: 'vertical',
						right: 10,
						top: 'center'
					},
					series: itemData
				});
			},
			formDataUpdate () {
				this.$refs['NodePanelDialogForm'].validate((valid, errs) => {
					if (valid) {
						this.$emit('data-update', this.form);
						this.closeDialog();
					};
				});
			},
			closeDialog () {
				// this.$emit('update:scope-mode', 'edit');
				this.$emit('update:dialogVisible', false);
				this.$emit('update:scope-data', {});
				if (this.itemZone){
					this.itemZone.dispose();
				}
			},
			openDialog () {
				this.form = {...this.formData};
				this.formData.properties.params.forEach((itm, itmId)=>{
					this.form.properties.params[itmId] = {
						...itm,
						changed: false
					}
				})
				if (this.scopeMode == "scope" && setDefault(setDefault(this.scopeData, {}).data, []).length > 0){
					this.$nextTick(() => {
						this.initItemZone(
							setDefault(setDefault(this.scopeData, {}).data, []),
							setDefault(setDefault(this.scopeData, {}).timeData, [])
						);
					});
				}
			},
			closeClickDialog () {
				this.clickParam = "";
				this.clickValue = null; 
				if (this.itemClickZone){
					this.itemClickZone.dispose();
				}
				this.clickDialogShow = false;
			},
			openClickDialog () {
				this.$nextTick(() => {
					//console.log(this.$refs.dialogparamitemarea)
					this.itemClickZone = echarts.init(this.$refs.dialogparamitemarea);
					if (this.clickValue.ndim == 1){
						this.itemClickZone.setOption({
							tooltip: {
								trigger: 'axis',
								formatter: function (p) {
									return p[0].seriesName + "(" + p[0].value[0] + ") = " + p[0].value[1];
								  }
							},
							grid: {
								left: '15%',
								right: '15%',
								bottom: '10%'
							},
							xAxis: {
								//data: timeData,
								type: 'value',
							},
							yAxis: {
								type: 'value',
							},
							toolbox: {
								right: 10,
								feature: {
									restore: {},
									saveAsImage: {}
								}
							},
							series: [{
								name: this.clickParam,
								data: this.clickValue.x.map((itm, itmId)=>{
									return [itm, this.clickValue.y[itmId]];
								}),
								smooth: true,
								type: "line",
							}]
						});
					} else {
						let data = [];
						this.clickValue.y.forEach((y_itm, x1_ind)=>{
							y_itm.forEach((vitm, x2_ind)=>{
								data.push([this.clickValue.x1[x1_ind], this.clickValue.x2[x2_ind], vitm]);
							})
						})
						this.itemClickZone.setOption({
							tooltip: {
								formatter: function (p) {
									return p.seriesName + "(" + p.data[0] + "," + p.data[1] + ") = " + p.data[2];
								  }
							},
							grid: {
								left: '15%',
								right: '15%',
								bottom: '10%'
							},
							xAxis: {
								//data: timeData,
								type: 'category',
							},
							yAxis: {
								type: 'category',
							},
							toolbox: {
								right: 10,
								feature: {
									restore: {},
									saveAsImage: {}
								}
							},
							visualMap: {
								min: Math.min(...(this.clickValue.y.map(itm=>Math.min(...(itm.map(it=>{if(it===null){return Infinity}else{return it}})))))),
								max: Math.max(...(this.clickValue.y.map(itm=>Math.max(...(itm.map(it=>{if(it===null){return -Infinity}else{return it}})))))),
								calculable: true,
								realtime: false,
								inRange: {
								color: [
									'#313695',
									'#4575b4',
									'#74add1',
									'#abd9e9',
									'#e0f3f8',
									'#ffffbf',
									'#fee090',
									'#fdae61',
									'#f46d43',
									'#d73027',
									'#a50026'
								]
								}
							},
							series: [{
								name: this.clickParam,
								data: data,
								smooth: true,
								type: "heatmap",
								animation: false,
								emphasis: {
									itemStyle: {
										borderColor: '#333',
										borderWidth: 1
									}
								},
							}]
						});
					}
				})
			},
		}
	});

let controlPanel = Vue.component(
	'ate-bond-control-panel',
	{
		props: ["lf", "zoomInable", "zoomOutable", "zoomResetable", "translateRestable",
				"resetable", "undoable", "redoable", "clearable", "reDrawable",
				"importDatable", "exportDatable", "exportStructable", "analysable"],
		template: `
			<div style="left: 250px;">
				<el-button-group>
					<el-button v-if="setDefault(zoomInable, true)" type="plain" size="small" @click="$_zoomIn">放大</el-button>
					<el-button v-if="setDefault(zoomOutable, true)" type="plain" size="small" @click="$_zoomOut">缩小</el-button>
					<el-button v-if="setDefault(zoomResetable, true)" type="plain" size="small" @click="$_zoomReset">大小适应</el-button>
					<el-button v-if="setDefault(translateRestable, true)" type="plain" size="small" @click="$_translateRest">定位还原</el-button>
					<el-button v-if="setDefault(resetable, true)" type="plain" size="small" @click="$_reset">还原(大小&定位)</el-button>
					<el-button v-if="setDefault(undoable, true)" type="plain" size="small" @click="$_undo" :disabled="undoDisable">撤销(ctrl+z)</el-button>
					<el-button v-if="setDefault(redoable, true)" type="plain" size="small" @click="$_redo" :disabled="redoDisable">重做(ctrl+y)</el-button>
					<el-button v-if="setDefault(clearable, true)" type="plain" size="small" @click="$_clear">清空</el-button>
					<el-button v-if="setDefault(reDrawable, true)" type="plain" size="small" @click="$_reDraw">重绘</el-button>
					<el-button v-if="setDefault(exportDatable, true)" type="plain" size="small" @click="$_exportData">导出</el-button>

					<el-upload v-if="setDefault(importDatable, true)" style="display:inline-block; margin-left: -5px;"
						action="" :auto-upload="false" accept=".json" :multiple="false" :show-file-list="false"
						:on-change="$_importData">
						<el-button type="plain" size="small">载入</el-button>
					</el-upload>

					<el-upload v-if="setDefault(analysable, true)" style="display:inline-block; margin-left: -5px;" action=""
						:auto-upload="false" accept=".csv" :multiple="false" :show-file-list="false" :on-change="$_analyse">
						<el-button type="plain" size="small">仿真</el-button>
					</el-upload>
				</el-button-group>
			</div>
		`,
		data() {
			return {
				undoDisable: true,
				redoDisable: true,
				Visible: false,
				fileName: '键合图模型配置',
				scopeData: {},
			}
		},
		methods: {
			importStruct,
			setDefault,
			$_zoomIn () {
				this.$props.lf.zoom(true);
			},
			$_zoomOut () {
				this.$props.lf.zoom(false);
			},
			$_zoomReset () {
				this.$props.lf.resetZoom();
			},
			$_translateRest () {
				this.$props.lf.resetTranslate();
			},
			$_reset () {
				this.$props.lf.resetZoom();
				this.$props.lf.resetTranslate();
			},
			$_undo () {
				this.$props.lf.undo();
			},
			$_redo () {
				this.$props.lf.redo();
			},
			$_clear () {
				this.$props.lf.clearData();
			},
			$_reDraw () {
				let data = this.$props.lf.getGraphRawData()
				data.nodes.forEach((node) => {
					node.properties.showType = 'edit';
				});
				this.$props.lf.render(data);
			},
			$_exportData () {
				let data = exportStruct(this.$props.lf.getGraphData());
				this.jsonText = JSON.stringify(data, null, 4);
				let a_element = document.createElement('a');
				a_element.download = this.fileName + '.json';
				a_element.href = window.URL.createObjectURL(new Blob([this.jsonText], {type: 'text/json' }));
				a_element.dispatchEvent(new MouseEvent('click'));
				window.URL.revokeObjectURL(a_element.href);
			},
			$_importData (file) {
				return new Promise((resolve, reject) => {
					// 检验是否支持 FileRender
					if (typeof FileReader === 'undefined') {
						reject('当前浏览器不支持FileReader');
					};
					// 执行读取json数据操作
					let reader = new FileReader();
					reader.readAsText(file.raw);
					reader.onerror = (error) => {
						reject('读取键合图模型文件解析失败', error);
					};
					reader.onload = () => {
						if (reader.result) {
							try {
								resolve(JSON.parse(reader.result));
							} catch (error) {
								reject('读取键合图模型文件解析失败', error);
							};
						} else {
							reject('读取键合图模型文件解析失败', error);
						};
					};
				}).then((res) => {
					let data = importStruct(res);
					this.$props.lf.render(data);
					this.$message({
						type: 'success',
						duration: 20,
						message: "解析完毕",
					})
				}).catch(err=>{
					console.warn(err)
					this.$message({
						type: 'error',
						duration: 30,
						message: "解析失败",
					})
				});
			},
			$_analyse (file) {
				let loading = this.$loading({
					lock: true,
					text: '仿真中，请稍候...',
					spinner: 'el-icon-loading',
					background: 'rgba(0, 10, 0, 0.5)'
				})
				let fd = new FormData();
				fd.append('graphStruct', JSON.stringify(exportStruct(this.$props.lf.getGraphData())));
				fd.append('dataFile', file.raw);
				/**Object.entries(this.$props.lf.paramsFile).forEach((itm, _)=>{
					itm[1].forEach((param, paramId)=>{
						if (param.isFunc){
							fd.append(itm[0]+paramId, param.value)
						}
					})
				})*/
				axios.post('/bond-analyse/analyse-data/', fd)
				.then((response) => {
					let res = response.data;
					this.$props.lf.render(importStruct(res, 'simulate'));
					this.$props.lf.timeData = res.timeData;
					this.$props.lf.scopeMode = "scope";
					loading.close();
				}).catch(e=>{
					loading.close();
				});
			},
			/**$_exportStruct () {
				let fd = new FormData();
				fd.append('graphStruct', JSON.stringify(exportStruct(this.$props.lf.getGraphData())));
				Object.entries(this.$props.lf.paramsFile).forEach((itm, _)=>{
					itm[1].forEach((param, paramId)=>{
						if (param.isFunc){
							fd.append(itm[0]+paramId, param.value.raw)
						}
					})
				})
				axios.post('/bond-analyse/export-config-complet/', fd)
				.then((response) => {
					let jsonText = response.data;
					let a_element = document.createElement('a');
					a_element.download = this.fileName + '.json';
					a_element.href = window.URL.createObjectURL(new Blob([jsonText], {type: 'text/json' }));
					a_element.dispatchEvent(new MouseEvent('click'));
					window.URL.revokeObjectURL(a_element.href);
				});
				this.$props.lf.scopeMode = "scope";
			},*/
		},
	});

let bondComp = Vue.component(
	'ate-bond-graph',
	{
		props: ["zoomIn", "zoomOut", "zoomReset", "translateRest",
				"reset", "undo", "redo", "clear", "reDraw", "exportData",
				"importData", "importFMECA", "configCheck", "optimizeCkpt",
				"analyse"],
		components: {
			'ate-bond-node-panel': nodePanel,
			'ate-bond-edit-dialog': editDialog,
			'ate-bond-control-panel': controlPanel,
		},
		template: `
			<div class="logic-flow-view">
				<ate-bond-control-panel class="demo-control" v-if="lf" :lf="lf"
					:zoomIn="setDefault(zoomIn, true)" :zoomOut="setDefault(zoomOut, true)"
					:zoomReset="setDefault(zoomReset, true)" :translateRest="setDefault(translateRest, true)"
					:reset="setDefault(reset, true)" :undo="setDefault(undo, true)"
					:redo="setDefault(redo, true)" :clear="setDefault(clear, true)"
					:reDraw="setDefault(reDraw, true)" :exportData="setDefault(exportData, true)"
					:importData="setDefault(importData, true)" :analyse="setDefault(analyse, true)" />
				<ate-bond-node-panel v-if="lf" :lf="lf" />
				<div ref="container" class="LF-view" style="height: 90dvh; width=80dvw;"></div>
				<ate-bond-edit-dialog
					:dialog-visible.sync="dialogVisible"
					:scope-mode="(lf && lf.scopeMode) || 'edit'"
					:form-data="formData"
					:scope-data="scopeItemData"
					@data-update="$_dataUpdate" />
			</div>
		`,
		data() {
			return {
				lf: null,
				dialogVisible: false,
				formData: {},
				timeData: [],
				scopeItemData: {},
			}
		},
		mounted () {
			this.$_initLf();
			this.lf.scopeMode = "edit";
		},
		methods: {
			...uiMethods,
			setDefault,
			$_dataUpdate (_node) {
				let node = this.lf.graphModel.getNodeModelById(_node.id)
				node.updateText(_node.text.value)
				node.setProperties({..._node.properties})
				/**if (this.lf.paramsFile === undefined){
					this.lf.paramsFile = {};
				}
				this.lf.paramsFile[_node.id] = _node.properties.params.map((itm,itmId)=>{
					if (itm.changed || !(Object.keys(this.lf.paramsFile).includes(_node.id))){
						return itm;
					}  else {
						return this.lf.paramsFile[_node.id][itmId];
					}
				})*/
			},
		},
	});

export { bondComp }